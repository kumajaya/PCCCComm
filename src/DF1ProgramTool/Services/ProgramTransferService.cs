using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using DF1ProgramTool.Utilities;
using DF1ProgramTool.Models;
using PCCCComm;

namespace DF1ProgramTool.Services;

public class ProgramTransferService
{
    private readonly global::PCCCComm.PCCCComm _df1;
    private readonly IProgress<string>? _progressMessage;
    private readonly IProgress<double>? _progressPercent;
    private readonly CancellationToken _cancellationToken;
    private readonly PlcInfo? _plcInfo;

    public ProgramTransferService(
        global::PCCCComm.PCCCComm df1,
        IProgress<string>? message = null,
        IProgress<double>? percent = null,
        CancellationToken cancellation = default,
        PlcInfo? plcInfo = null)
    {
        _df1 = df1 ?? throw new ArgumentNullException(nameof(df1));
        _progressMessage = message;
        _progressPercent = percent;
        _cancellationToken = cancellation;
        _plcInfo = plcInfo;
    }

    public async Task UploadToFileAsync(string filePath)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _progressMessage?.Report("Reading program directory…");

            long totalBytes = 0;
            long transferredBytes = 0;
            int totalFiles = 0;
            int completedFiles = 0;

            // Subscribe to new FileProgress event
            EventHandler<global::PCCCComm.PCCCComm.FileProgressEventArgs> progressHandler = (_, e) =>
            {
                completedFiles = e.FilesCompleted;
                totalFiles = e.TotalFiles;
                transferredBytes = e.TotalBytesTransferred;
                totalBytes = e.GrandTotalBytes;
                
                double pct = totalBytes > 0 
                    ? (double)transferredBytes / totalBytes * 100.0
                    : (double)completedFiles / totalFiles * 100.0;
                
                _progressPercent?.Report(Math.Min(pct, 95.0));
                _progressMessage?.Report($"Uploading {completedFiles} of {totalFiles} files ({transferredBytes}/{totalBytes} bytes)…");
            };

            _df1.FileProgress += progressHandler;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                var files = _df1.UploadProgramData();

                _progressPercent?.Report(95);
                _progressMessage?.Report($"Saving {files.Count} file(s) to disk…");

                if (_plcInfo != null)
                    SaveToFile(filePath, files,
                        _plcInfo.ProcessorType,
                        _plcInfo.Family,
                        _plcInfo.SeriesRevision,
                        _plcInfo.RamKb,
                        _plcInfo.Bulletin);
                else
                    SaveToFile(filePath, files);

                _progressPercent?.Report(100);
                _progressMessage?.Report($"Upload complete – {files.Count} program file(s), {totalBytes} bytes.");
            }
            finally
            {
                _df1.FileProgress -= progressHandler;
            }
        }, _cancellationToken);
    }

    public async Task DownloadFromFileAsync(string filePath, int targetProcessorType, 
        string targetBulletin, bool skipSetProgramMode = false)
    {
        _cancellationToken.ThrowIfCancellationRequested();

        await Task.Run(() =>
        {
            _progressMessage?.Report("Reading program file from disk…");

            // Validate against target PLC
            var files = LoadFromFileAndValidate(filePath, targetProcessorType, targetBulletin, requireBulletinMatch: true);
            
            long totalBytes = files.Sum(f => f.Data?.Length ?? 0);

            if (!skipSetProgramMode)
            {
                // PCCCComm.SetProgramMode() throws on failure; let the exception propagate
                _progressMessage?.Report($"Setting PLC to Program mode…");
                _df1.SetProgramMode();
            }

            _cancellationToken.ThrowIfCancellationRequested();

            EventHandler<global::PCCCComm.PCCCComm.FileProgressEventArgs> progressHandler = (_, e) =>
            {
                double pct = e.GrandTotalBytes > 0 
                    ? (double)e.TotalBytesTransferred / e.GrandTotalBytes * 100.0
                    : (double)e.FilesCompleted / e.TotalFiles * 100.0;
                
                _progressPercent?.Report(pct);
                _progressMessage?.Report($"Downloading {e.FilesCompleted} of {e.TotalFiles} files ({e.TotalBytesTransferred}/{e.GrandTotalBytes} bytes)…");
            };

            _df1.FileProgress += progressHandler;
            try
            {
                _cancellationToken.ThrowIfCancellationRequested();
                _df1.DownloadProgramData(files);

                _progressPercent?.Report(100);
                _progressMessage?.Report($"Download complete – {files.Count} file(s), {totalBytes} bytes.");
            }
            finally
            {
                _df1.FileProgress -= progressHandler;
            }
        }, _cancellationToken);
    }

    // ─── Binary serialisation ─────────────────────────────────────────────────

    private static void SaveToFile(string path, Collection<PLCFileDetails> files)
    {
        // Save with permissive defaults (no processor/bulletin check)
        SaveToFile(path, files, processorType: 0, familyTag: "PLC", seriesRev: 0, ramKb: 0, bulletin: string.Empty);
    }

    private static void SaveToFile(string path, Collection<PLCFileDetails> files,
        int processorType, string familyTag, byte seriesRev, byte ramKb, string bulletin)
    {
        using var ms = new MemoryStream();
        using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            bw.Write((ushort)0xDF1A);                 // magic
            bw.Write((byte)1);                        // version (current = 1)
            bw.Write(processorType);                  // int32
            bw.Write(seriesRev);                      // byte
            bw.Write(ramKb);                          // byte
            var tagBytes = System.Text.Encoding.ASCII.GetBytes((familyTag ?? "PLC").PadRight(8).Substring(0,8));
            bw.Write(tagBytes);                       // 8 bytes family tag
            var bBytes = System.Text.Encoding.UTF8.GetBytes(bulletin ?? "");
            bw.Write(bBytes.Length);
            bw.Write(bBytes);
            bw.Write(DateTime.UtcNow.ToBinary());     // timestamp
            bw.Write(files.Count);

            foreach (var file in files)
            {
                bw.Write(file.FileNumber);
                bw.Write(file.FileType);
                bw.Write(file.NumberOfBytes);
                bw.Write(file.Data.Length);
                bw.Write(file.Data);
            }
        }

        byte[] content = ms.ToArray();
        uint crc = Crc32.Compute(content);
        byte[] sha;
        using (var sha256 = SHA256.Create())
            sha = sha256.ComputeHash(content);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        fs.Write(content, 0, content.Length);
        using var bw2 = new BinaryWriter(fs);
        bw2.Write(crc);
        bw2.Write(sha);
    }

    private static Collection<PLCFileDetails> LoadFromFileAndValidate(string path)
    {
        // Call the full validator with permissive defaults (no processor/bulletin check)
        return LoadFromFileAndValidate(path, targetProcessorType: 0, targetBulletin: string.Empty, requireBulletinMatch: false);
    }

    private static Collection<PLCFileDetails> LoadFromFileAndValidate(
        string path, int targetProcessorType, string targetBulletin, bool requireBulletinMatch = false)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"Program file not found: {path}");

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        ushort magic = br.ReadUInt16();
        if (magic != 0xDF1A) throw new InvalidDataException("Not a DF1ProgramTool file (magic mismatch)");

        byte version = br.ReadByte();
        if (version != 1) throw new InvalidDataException($"Unsupported file version: {version} (expected 1)");

        int procType = br.ReadInt32();
        byte series = br.ReadByte();
        byte ramKb = br.ReadByte();
        string family = System.Text.Encoding.ASCII.GetString(br.ReadBytes(8)).Trim();
        int bulletLen = br.ReadInt32();
        string bulletin = bulletLen > 0 ? System.Text.Encoding.UTF8.GetString(br.ReadBytes(bulletLen)) : string.Empty;
        long ts = br.ReadInt64();
        int count = br.ReadInt32();

        if (count < 0 || count > 10_000)
            throw new InvalidDataException($"Unexpected file count: {count}");

        var files = new Collection<PLCFileDetails>();
        for (int i = 0; i < count; i++)
        {
            var file = new PLCFileDetails
            {
                FileNumber    = br.ReadInt32(),
                FileType      = br.ReadInt32(),
                NumberOfBytes = br.ReadInt32()
            };
            int dataLen = br.ReadInt32();
            if (dataLen < 0 || dataLen > 1_000_000)
                throw new InvalidDataException($"Invalid data length at file {i}: {dataLen}");
            file.Data = br.ReadBytes(dataLen);
            files.Add(file);
        }

        const int MinFileSize = 69; // header minimum + trailer (CRC32 + SHA256)
        if (fs.Length < MinFileSize)
            throw new InvalidDataException(
                $"File too small to be valid (size={fs.Length}, minimum={MinFileSize})");
        fs.Seek(-36, SeekOrigin.End);
        uint storedCrc = br.ReadUInt32();
        byte[] storedSha = br.ReadBytes(32);

        fs.Seek(0, SeekOrigin.Begin);
        byte[] allExceptTrailer = new byte[fs.Length - 36];
        int read = fs.Read(allExceptTrailer, 0, allExceptTrailer.Length);
        if (read != allExceptTrailer.Length) throw new IOException("Failed to read file for checksum");

        uint calcCrc = Crc32.Compute(allExceptTrailer);
        if (calcCrc != storedCrc) throw new InvalidDataException("CRC mismatch: file may be corrupted");

        using var sha256 = SHA256.Create();
        var calcSha = sha256.ComputeHash(allExceptTrailer);
        if (!calcSha.SequenceEqual(storedSha)) throw new InvalidDataException("SHA256 mismatch: file integrity check failed");

        if (targetProcessorType != 0 && procType != targetProcessorType)
            throw new InvalidDataException($"ProcessorType mismatch: file=0x{procType:X2} target=0x{targetProcessorType:X2}");

        if (requireBulletinMatch && !string.Equals(bulletin, targetBulletin, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Bulletin mismatch: file='{bulletin}' target='{targetBulletin}'");

        return files;
    }

    /// <summary>
    /// Compares both structure and data (via CRC32) between a backup file and the PLC.
    /// Only files that exist on both sides and have matching sizes are data-compared.
    /// </summary>
    /// <param name="filePath">Path to the .bin backup file</param>
    /// <returns>List of full comparison results</returns>
    public async Task<List<FullCompareResult>> CompareFullAsync(string filePath)
    {
        return await Task.Run(() =>
        {
            _progressMessage?.Report("Reading backup file…");
            var fileFiles = LoadFromFileAndValidate(filePath, 0, "", false);
            
            _progressMessage?.Report("Reading PLC program directory…");
            
            long totalBytes = 0;
            long transferredBytes = 0;
            int totalFiles = 0;
            int completedFiles = 0;

            // Subscribe to FileProgress to track PLC upload progress
            EventHandler<global::PCCCComm.PCCCComm.FileProgressEventArgs> progressHandler = (_, e) =>
            {
                completedFiles = e.FilesCompleted;
                totalFiles = e.TotalFiles;
                transferredBytes = e.TotalBytesTransferred;
                totalBytes = e.GrandTotalBytes;
                
                double pct = totalBytes > 0 
                    ? (double)transferredBytes / totalBytes * 100.0
                    : (double)completedFiles / totalFiles * 100.0;
                
                _progressPercent?.Report(pct);
                _progressMessage?.Report($"Reading PLC files: {completedFiles} of {totalFiles} ({transferredBytes}/{totalBytes} bytes)…");
            };

            _df1.FileProgress += progressHandler;
            try
            {
                var plcFiles = _df1.UploadProgramData();
                
                _progressPercent?.Report(90);
                _progressMessage?.Report("Comparing files…");

                var comparer = EqualityComparer<PLCFileDetails>.Default;
                var results = new List<FullCompareResult>();

                foreach (var file in fileFiles)
                {
                    _cancellationToken.ThrowIfCancellationRequested();
                    
                    var plcFile = plcFiles.FirstOrDefault(f => f.FileNumber == file.FileNumber && f.FileType == file.FileType);
                    bool existsInPlc = !comparer.Equals(plcFile, default);
                    bool sizeMatches = existsInPlc && plcFile.NumberOfBytes == file.NumberOfBytes;
                    uint? fileCrc = null, plcCrc = null;
                    bool dataMatches = false;

                    if (existsInPlc && sizeMatches)
                    {
                        fileCrc = Crc32.Compute(file.Data);
                        plcCrc = Crc32.Compute(plcFile.Data);
                        dataMatches = (fileCrc == plcCrc);
                    }

                    results.Add(new FullCompareResult
                    {
                        FileNumber = file.FileNumber,
                        FileType = file.FileType,
                        FileTypeName = FileTypeHelper.GetFileTypeName(file.FileType),
                        FileExistsInFile = true,
                        FileExistsInPlc = existsInPlc,
                        SizeMatches = sizeMatches,
                        FileSizeBytes = file.NumberOfBytes,
                        PlcSizeBytes = existsInPlc ? plcFile.NumberOfBytes : (int?)null,
                        FileCrc32 = fileCrc,
                        PlcCrc32 = plcCrc,
                        DataMatches = dataMatches
                    });
                }

                // Files in PLC but not in backup
                foreach (var plcFile in plcFiles)
                {
                    if (!fileFiles.Any(f => f.FileNumber == plcFile.FileNumber && f.FileType == plcFile.FileType))
                    {
                        results.Add(new FullCompareResult
                        {
                            FileNumber = plcFile.FileNumber,
                            FileType = plcFile.FileType,
                            FileTypeName = FileTypeHelper.GetFileTypeName(plcFile.FileType),
                            FileExistsInFile = false,
                            FileExistsInPlc = true,
                            SizeMatches = false,
                            FileSizeBytes = null,
                            PlcSizeBytes = plcFile.NumberOfBytes,
                            DataMatches = false
                        });
                    }
                }

                _progressPercent?.Report(100);
                _progressMessage?.Report($"Compare complete – {fileFiles.Count} files in backup, {plcFiles.Count} files in PLC.");

                return results
                    .OrderBy(r => r.FileType == 0 ? 0 : 1)
                    .ThenBy(r => (r.FileType >= 0x80 && r.FileType <= 0x9F) ? 1 : 2)
                    .ThenBy(r => r.FileNumber)
                    .ThenBy(r => r.FileType)
                    .ToList();
            }
            finally
            {
                _df1.FileProgress -= progressHandler;
            }
        }, _cancellationToken);
    }
}
