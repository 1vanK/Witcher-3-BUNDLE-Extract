using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip.Compression;
using DobozS;
using Snappy.Sharp;
using LZ4ps;
using System.Security.Cryptography;

// Информация об упакованном файле
class FileMetadata
{
    public string fileName = null;
    public byte[] hash = null;
    public uint uncompressedSize = 0;
    public uint compressedSize = 0; // Если файл не сжат, то совпадает с uncompressedSize
    public uint offset = 0;
    public uint date = 0;
    public uint time = 0;
    public uint crc32 = 0; // Контрольная сумма для распакованных данных
    public uint compressionMethod = 0;
}

static class Program
{
    // В целях выравнивания некоторые блоки данных дополняются (padding) байтами,
    // не несущими смысловой нагрузки: https://en.wikipedia.org/wiki/Data_structure_alignment
    
    // Возвращает выровненный размер (size + padding)
    static uint Align(uint size, uint value)
    {
        // Выравнивание бывает длинным (0x1000u) и коротким (0x10u).
        // В некоторых BUNDLE-файлах используется только короткое выравнивание
        if (value != 0x1000u && value != 0x10u)
            throw new Exception("value != 0x1000u && value != 0x10u");

        if (size % value == 0u)
            return size; // Уже выровнено

        return (size / value + 1u) * value;
    }

    // Возвращает байты, которые используются для выравнивания
    static byte[] Padding(uint size, uint value)
    {
        uint fullPaddingLength = Align(size, value) - size;
        byte[] result = new byte[fullPaddingLength]; // Автоматически заполняется нулями

        // До 0x10u дополняется байтами "AlignmentUnused", а остаток - нули
        uint shortPaddingLength = Align(size, 0x10u) - size;
        byte[] phrase = Encoding.ASCII.GetBytes("AlignmentUnused");
        for (int i = 0; i < shortPaddingLength; i++)
            result[i] = phrase[i];

        return result;
    }

    
    // Нет никакой уверенности, что дата и время извлекаются именно так,
    // ибо проверить невозможно
    static DateTime TimestampToDateTime(uint date, uint time)
    {
        if ((date & 0b00000000_00000000_00000011_11111111) != 0)
            throw new Exception("(date & 0b00000000_00000000_00000011_11111111) != 0");

        int year = (int)(date >> 20);           // 11111111 11110000 00000000 00000000
        int month = (int)(date >> 15 & 0x1F);   // 00000000 00001111 10000000 00000000
        int day = (int)(date >> 10 & 0x1F);     // 00000000 00000000 01111100 00000000

        if ((time & 0b11111000_00000000_00000000_00000000) != 0)
            throw new Exception("(time & 0b11111000_00000000_00000000_00000000) != 0");

        int hour = (int)(time >> 22);           // 00000111 11000000 00000000 00000000
        int minute = (int)(time >> 16 & 0x3F);  // 00000000 00111111 00000000 00000000
        int second = (int)(time >> 10 & 0x3F);  // 00000000 00000000 11111100 00000000
        int millisecond = (int)(time & 0x3FF);  // 00000000 00000000 00000011 11111111

        if (hour > 23 || minute > 59 || second > 59 || millisecond > 999)
            throw new Exception("hour > 23 || minute > 59 || second > 59 || millisecond > 999");

        return new DateTime(year, month, day, hour, minute, second);
    }

    static List<FileMetadata> Parse(BinaryReader reader)
    {
        // +++++++++++++++++++++++++ Начало заголовка +++++++++++++++++++++++++

        // Файл начинается с POTATO70 (8 байт)
        ulong signature = reader.ReadUInt64();
        if (signature != 0x30374F5441544F50) // POTATO70
            throw new Exception("signature != 0x30374F5441544F50");

        // Размер BUNDLE-файла (4 байта)
        uint size = reader.ReadUInt32();
        if (size != reader.BaseStream.Length)
            throw new Exception("size != reader.BaseStream.Length");

        // Размер каждого сжатого файла выравнивается до 0x10u, все это суммируется и получается это значение
        uint dataSize = reader.ReadUInt32();

        // Размер блока с метаданными обо всех упакованных файлах (4 байта)
        uint metaSize = reader.ReadUInt32();

        // Что-то (4 байта)
        uint something0 = reader.ReadUInt32();
        if (something0 != 0x10003) // Всегда 65539
            throw new Exception("something0 != 0x10003");
        
        // Что-то (4 байта)
        uint something1 = reader.ReadUInt32();
        if (something1 != 0x13131300) // Всегда 320017152
            throw new Exception("something1 != 0x13131300");

        // Что-то (4 байта)
        uint something2 = reader.ReadUInt32();
        if (something2 != 0x13131313) // Всегда 320017171
            throw new Exception("something2 != 0x13131313");

        // Заголовок занимает 32 байта
        const uint headerSize = 32;

        if (reader.BaseStream.Position != headerSize)
            throw new Exception("reader.BaseStream.Position != headerSize");

        // ------------------------------- Конец заголовка -------------------------------

        // ++++++++++++++++++++++++++++++++ Начало информации об упакованных файлах ++++++++++++++++++++++++++++++++

        List<FileMetadata> result = new List<FileMetadata>();

        // Запись о каждом файле занимает 320 байт
        while (reader.BaseStream.Position < metaSize + headerSize)
        {
            FileMetadata metadata = new FileMetadata();

            // Название файла (нуль-терминированная строка, 256 байт)
            byte[] fileNameBytes = reader.ReadBytes(256);
            metadata.fileName = Encoding.ASCII.GetString(fileNameBytes).TrimEnd('\0');

            // Хэш (16 байт). Похоже на MD5, но не является MD5 сжатых данных
            metadata.hash = reader.ReadBytes(16);
            
            // Что-то (4 байта)
            uint something3 = reader.ReadUInt32();
            if (something3 != 0u) // Всегда ноль
                throw new Exception("something3 != 0u");

            // Реальный размер файла (4 байта)
            metadata.uncompressedSize = reader.ReadUInt32();

            // Размер файла в сжатом виде (4 байта)
            metadata.compressedSize = reader.ReadUInt32();

            // Позиция, с которой начинается содержимое файла
            metadata.offset = reader.ReadUInt32();

            // Дата и время файла (8 байт)
            metadata.date = reader.ReadUInt32();
            metadata.time = reader.ReadUInt32();

            // Что-то (16 байт)
            byte[] something4 = reader.ReadBytes(16);
            for (int i = 0; i < 16; i++) // Всегда 16 нулевых байтов
            {
                if (something4[i] != 0)
                    throw new Exception("something4[i] != 0");
            }

            // Контрольная сумма распакованных данных (4 байта)
            metadata.crc32 = reader.ReadUInt32();

            // Алгоритм сжатия файла (4 байта)
            metadata.compressionMethod = reader.ReadUInt32();
            if (metadata.compressionMethod > 5)
                throw new Exception("Incorrect compressionMethod");

            result.Add(metadata);
        }

        if (reader.BaseStream.Position != headerSize + metaSize)
            throw new Exception("reader.BaseStream.Position != metaSize + headerSize");

        // --------------------------------- Конец информации об упакованных файлах ---------------------------------

        // ++++++++ Разные проверки, чтобы убедиться, что в BUNDLE-файле больше нет каких-то неизвестных данных ++++++++

        uint alignValue = 0x1000u;

        // После метаданных расположены выравнивающие байты,
        // а затем сразу же начинается первый упакованный файл
        if (result[0].offset != Align(headerSize + metaSize, alignValue))
        {
            // В некоторых файлах только короткое выравнивание
            alignValue = 0x10u;

            // Пробуем снова с более коротким выравниванием
            if (result[0].offset != Align(headerSize + metaSize, alignValue))
                throw new Exception("result[0].offset != Align(headerSize + metaSize, alignValue)");
        }

        // Перед каждым упакованным файлом расположены выравнивающие байты.
        // Вычисляем начало каждого упакованного файла по данным предыдущего файла
        for (int i = 1; i < result.Count; i++)
        {
            uint calculatedOffset = result[i - 1].offset + Align(result[i - 1].compressedSize, alignValue);
            if (calculatedOffset != result[i].offset)
            {
                if (alignValue == 0x10u) // Выравнивание уже и так короткое
                    throw new Exception("alignValue == 0x10u");

                // Пробуем снова с более коротким выравниванием
                alignValue = 0x10u;
                calculatedOffset = result[i - 1].offset + Align(result[i - 1].compressedSize, alignValue);
                if (calculatedOffset != result[i].offset)
                    throw new Exception("calculatedOffset != result[i].offset");
            }
        }

        // После последнего файла расположены выравнивающие байты (выравнивание всегда короткое)
        uint calculatedFileEnd = result[result.Count - 1].offset + Align(result[result.Count - 1].compressedSize, 0x10u);
        if (calculatedFileEnd != reader.BaseStream.Length)
            throw new Exception("calculatedFileEnd != reader.BaseStream.Length");

        // Еще раз проверим размер файла
        uint calculatedFileSize = Align(headerSize + metaSize, alignValue);
        for (int i = 0; i < result.Count; i++)
        {
            FileMetadata metadata = result[i];

            if (i != result.Count - 1)
                calculatedFileSize += Align(metadata.compressedSize, alignValue);
            else
                calculatedFileSize += Align(metadata.compressedSize, 0x10u); // В конце файла выравнивание всегда короткое
        }

        if (calculatedFileSize != reader.BaseStream.Length)
            throw new Exception("calculatedFileSize != reader.BaseStream.Length");

        // ---------------------------------------- Конец проверок ----------------------------------------

        // +++++++++++++++ Можно еще проверить сами выравнивающие байты +++++++++++++++

        // Выравнивающие байты после метаданных
        {
            byte[] referencePadding = Padding(headerSize + metaSize, alignValue); // Генерируем оразец
            reader.BaseStream.Position = headerSize + metaSize;
            byte[] padding = reader.ReadBytes(referencePadding.Length); // Байты из файла
            if (!padding.SequenceEqual(referencePadding)) // Сравниваем с образцом
                Console.WriteLine("!padding.Equals(referencePadding)");
        }

        // Выравнивающие байты после каждого упакованного файла
        for (int i = 0; i < result.Count; i++)
        {
            FileMetadata metadata = result[i];

            // После последнего упакованного файла всегда укороченное выравнивание
            uint v = (i != result.Count - 1) ? alignValue : 0x10u;

            byte[] referencePadding = Padding(metadata.compressedSize, v);
            reader.BaseStream.Position = metadata.offset + metadata.compressedSize;
            byte[] padding = reader.ReadBytes(referencePadding.Length);
            if (!padding.SequenceEqual(referencePadding))
                Console.WriteLine("!padding.Equals(referencePadding)");
        }

        // -------------------- Конец проверки выравнивающих байтов --------------------

        // +++++++++++++++++++++++++++++++ Проверяем dataSize +++++++++++++++++++++++++++++++

        uint calculatedDataSize = 0;

        foreach (FileMetadata metadata in result)
            calculatedDataSize += Align(metadata.compressedSize, 0x10u);

        if (calculatedDataSize != dataSize)
            throw new Exception("calculatedDataSize != dataSize");

        // --------------------------- Конец проверки dataSize ---------------------------

        return result;
    }

    static void Save(BinaryReader input, FileMetadata metadata, BinaryWriter output)
    {
        input.BaseStream.Position = metadata.offset;
        byte[] compressedData = input.ReadBytes((int)metadata.compressedSize);
        byte[] uncompressedData = new byte[metadata.uncompressedSize];

        if (metadata.compressionMethod == 0) // Нет сжатия
        {
            if (metadata.compressedSize != metadata.uncompressedSize)
                throw new Exception("metadata.compressedSize != metadata.uncompressedSize");

            uncompressedData = compressedData;
        }
        else if (metadata.compressionMethod == 1) // Deflate
        {
            Inflater inflater = new Inflater(false);
            inflater.SetInput(compressedData);
            int length = inflater.Inflate(uncompressedData);

            if (length != metadata.uncompressedSize)
                throw new Exception("length != metadata.uncompressedSize");

            if (!inflater.IsFinished)
            {
                // Какой-то остаток в конце сжатых данных, но он не дает дополнительных распакованных данных
                byte[] remain = new byte[100];
                int remainLength = inflater.Inflate(remain);

                if (remainLength != 0 || !inflater.IsFinished)
                    throw new Exception("remainLength != 0 || !inflater.IsFinished");
            }
        }
        else if (metadata.compressionMethod == 2) // Snappy
        {
            SnappyDecompressor decompressor = new SnappyDecompressor();
            uncompressedData = decompressor.Decompress(compressedData, 0, compressedData.Length);
        }
        else if (metadata.compressionMethod == 3) // Doboz
        {
            uncompressedData = DobozDecoder.Decode(compressedData, 0, compressedData.Length);

            if (uncompressedData.Length != metadata.uncompressedSize)
                throw new Exception("uncompressedData.Length != metadata.uncompressedSize");
        }
        else if (metadata.compressionMethod == 4 || metadata.compressionMethod == 5) // LZ4
        {
            uncompressedData = LZ4Codec.Decode32(compressedData, 0, compressedData.Length, (int)metadata.uncompressedSize);

            if (uncompressedData.Length != metadata.uncompressedSize)
                throw new Exception("uncompressedData.Length != metadata.uncompressedSize");
        }
        else
        {
            throw new Exception("Unknown compression method");
        }

        // Проверяем, что распаковали данные правильно
        Crc32 calculatedCrc32 = new Crc32();
        calculatedCrc32.Update(uncompressedData);
        if (calculatedCrc32.Value != metadata.crc32)
            throw new Exception("calculatedCrc32.Value != metadata.crc32");

        output.Write(uncompressedData);
    }

    static void UnpackFile(string inputFile, string outDir)
    {
        Console.WriteLine(inputFile);

        try
        {
            using (BinaryReader reader = new BinaryReader(File.Open(inputFile, FileMode.Open)))
            {
                List<FileMetadata> metadatas = Parse(reader);
                foreach (FileMetadata metadata in metadatas)
                {
                    string outputFile = outDir + "\\" + metadata.fileName;
                    Directory.CreateDirectory(Path.GetDirectoryName(outputFile));

                    using (BinaryWriter writer = new BinaryWriter(File.Open(outputFile, FileMode.Create)))
                    {
                        Save(reader, metadata, writer);
                        writer.Close();
                        DateTime dateTime = TimestampToDateTime(metadata.date, metadata.time);
                        File.SetLastWriteTimeUtc(outputFile, dateTime);
                        File.SetCreationTimeUtc(outputFile, dateTime);
                    }
                }
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e.Message);
        }

    }

    static void UnpackDir(string inputDir, string outDir)
    {
        foreach (string fileName in Directory.GetFiles(inputDir, "*.bundle", SearchOption.AllDirectories))
            UnpackFile(fileName, outDir);
    }

    static void Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.WriteLine("Usage: \"input dir or file\" \"output dir\"");
            return;
        }

        if (File.Exists(args[0]))
            UnpackFile(args[0], args[1]);
        else
            UnpackDir(args[0], args[1]);

    }
}