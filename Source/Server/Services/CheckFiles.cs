using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OCUnion;
using OCUnion.Transfer;
using OCUnion.Transfer.Model;
using OCUnion.Transfer.Types;
using ServerOnlineCity.Model;
using Transfer;

namespace ServerOnlineCity.Services
{
    internal sealed class CheckFiles : IGenerateResponseContainer
    {
        public int RequestTypePackage => (int)PackageType.Request35ListFiles;

        public int ResponseTypePackage => (int)PackageType.Response36ListFiles;

        private static long GetMaxPacketSize()
        {
            var maxPacketSizeMb = ServerManager.ServerSettings?.MaxFileSyncPacketSizeMb ?? 5;
            if (maxPacketSizeMb < 1) maxPacketSizeMb = 1;
            if (maxPacketSizeMb > 256) maxPacketSizeMb = 256;
            return maxPacketSizeMb * 1024L * 1024L;
        }

        public ModelContainer GenerateModelContainer(ModelContainer request, ServiceContext context)
        {
            if (context.Player == null) return null;
            var result = new ModelContainer() { TypePacket = ResponseTypePackage };
            result.Packet = checkFiles((ModelModsFilesRequest)request.Packet, context);
            return result;
        }

        private ModelModsFilesResponse checkFiles(ModelModsFilesRequest packet, ServiceContext context)
        {
            if (!ServerManager.FileHashChecker.CheckedDirAndFiles.TryGetValue(
                packet.CodeRequest,
                out var checkedDirAndFile))
            {
                //отвечаем всё ок ничего не нужно синхронить
                return new ModelModsFilesResponse()
                {
                    Folder = new FolderCheck() { FolderType = packet.FolderType },
                    Files = new List<ModelFileInfo>(),
                    FoldersTree = new FoldersTree(),
                    TotalSize = 0
                };
            }

            var result = new List<ModelFileInfo>();

            if (packet.CodeRequest % 1000 == 0)
            {
                var maxPacketSize = GetMaxPacketSize();
                var allServerFiles = new HashSet<string>(checkedDirAndFile.HashFiles.Keys);
                var packetFiles = packet.Files ?? new List<ModelFileInfo>(0);
                var resumeFrom = packet.ResumeFrom;
                var canReplace = checkedDirAndFile.Settings.NeedReplace;
                long packetSize = 0;
                long totalSize = 0;

                foreach (var modelFile in packetFiles)
                {
                    var modelFileFileName = modelFile.FileName.ToLower();
                    if (FileHashChecker.FileNameContainsIgnored(modelFileFileName, checkedDirAndFile.IgnoredFiles
                        , checkedDirAndFile.IgnoredFolder))
                    {
                        continue;
                    }

                    if (checkedDirAndFile.HashFiles.TryGetValue(modelFileFileName, out ModelFileInfo fileInfo))
                    {
                        allServerFiles.Remove(modelFileFileName);

                        if (!ModelFileInfo.UnsafeByteArraysEquale(modelFile.Hash, fileInfo.Hash))
                        {
                            AddOrCountFile(
                                checkedDirAndFile.Settings.ServerPath,
                                fileInfo.FileName,
                                modelFileFileName,
                                resumeFrom,
                                canReplace,
                                maxPacketSize,
                                ref packetSize,
                                ref totalSize,
                                result);
                        }
                    }
                    else
                    {
                        // mark file for delete
                        // Если файл с таким именем не найден, помечаем файл на удаление
                        modelFile.Hash = null;
                        modelFile.NeedReplace = checkedDirAndFile.Settings.NeedReplace;
                        modelFile.ChunkOffset = 0;
                        modelFile.ChunkTotalSize = 0;
                        result.Add(modelFile);
                    }
                }

                lock (context.Player)
                {
                    // проверяем в обратном порядке: что бы у клиента были все файлы
                    if (allServerFiles.Any())
                    {
                        foreach (var fileName in allServerFiles)
                        {
                            if (FileHashChecker.FileNameContainsIgnored(fileName, checkedDirAndFile.IgnoredFiles
                                , checkedDirAndFile.IgnoredFolder))
                            {
                                continue;
                            }

                            context.Player.ApproveLoadWorldReason = false;

                            AddOrCountFile(
                                checkedDirAndFile.Settings.ServerPath,
                                checkedDirAndFile.HashFiles[fileName].FileName,
                                fileName,
                                resumeFrom,
                                canReplace,
                                maxPacketSize,
                                ref packetSize,
                                ref totalSize,
                                result);
                        }
                    }

                    // Если файлы не прошли проверку, помечаем флагом, запрет загрузки мира
                    if (result.Any())
                    {
                        context.Player.ApproveLoadWorldReason = false;
                    }
                }

                return new ModelModsFilesResponse()
                {
                    Folder = checkedDirAndFile.Settings,
                    Files = result,
                    // микроптимизация: если файлы не будут восстанавливаться, не отправляем обратно список папок
                    // на восстановление (десериализацию папок также тратится время)
                    FoldersTree = result.Any() ? checkedDirAndFile.FolderTree : new FoldersTree(),
                    TotalSize = totalSize,
                };
            }
            else
            {
                var addFile = GetFile(checkedDirAndFile.Settings.ServerPath, checkedDirAndFile.Settings.XMLFileName, true);
                addFile.NeedReplace = checkedDirAndFile.Settings.NeedReplace;
                result.Add(addFile);

                return new ModelModsFilesResponse()
                {
                    Folder = checkedDirAndFile.Settings,
                    Files = result,
                    FoldersTree = new FoldersTree(),
                    TotalSize = 0,
                    IgnoreTag = checkedDirAndFile.Settings.IgnoreTag
                };
            }
        }

        private void AddOrCountFile(
            string rootDir,
            string fileName,
            string fileNameLower,
            Dictionary<string, long> resumeFrom,
            bool needReplace,
            long maxPacketSize,
            ref long packetSize,
            ref long totalSize,
            List<ModelFileInfo> result)
        {
            var size = GetFileSize(rootDir, fileName);

            if (needReplace)
            {
                // Пустой файл тоже должен быть отправлен хотя бы как пустой payload,
                // иначе клиент никогда не создаст/не обнулит его.
                if (size == 0)
                {
                    result.Add(GetFile(rootDir, fileName, true, 0, 0));
                    return;
                }

                var resumeOffset = GetResumeOffset(resumeFrom, fileNameLower, size);
                var remaining = size - resumeOffset;
                if (remaining <= 0)
                {
                    resumeOffset = 0;
                    remaining = size;
                }

                totalSize += remaining;

                var freeInPacket = maxPacketSize - packetSize;
                if (freeInPacket <= 0) return;

                var chunkSize = Math.Min(remaining, freeInPacket);
                if (chunkSize <= 0) return;

                var addFile = GetFile(rootDir, fileName, true, resumeOffset, chunkSize);
                result.Add(addFile);
                packetSize += addFile.Size;
                return;
            }

            var canAddNow = packetSize == 0 || packetSize + size <= maxPacketSize;
            if (canAddNow)
            {
                var addFile = GetFile(rootDir, fileName, false);
                result.Add(addFile);
                packetSize += addFile.Size;
                totalSize += addFile.Size;
                //Loger.Log($"packetSize={packetSize} totalSize={totalSize}");
            }
            else
            {
                totalSize += size;
            }
        }

        private static long GetResumeOffset(Dictionary<string, long> resumeFrom, string fileNameLower, long fileSize)
        {
            if (resumeFrom == null)
            {
                return 0;
            }

            if (!resumeFrom.TryGetValue(fileNameLower, out var offset))
            {
                // Клиент/сервер могут по-разному нормализовать разделители путей.
                var altFileName = fileNameLower.Contains("\\")
                    ? fileNameLower.Replace('\\', '/')
                    : fileNameLower.Replace('/', '\\');
                if (!resumeFrom.TryGetValue(altFileName, out offset))
                {
                    return 0;
                }
            }

            if (offset <= 0 || offset >= fileSize)
            {
                return 0;
            }

            return offset;
        }

        private ModelFileInfo GetFile(string rootDir, string fileName, bool needReplace, long chunkOffset = 0, long chunkSize = long.MaxValue)
        {
            var newFile = new ModelFileInfo() { FileName = fileName, NeedReplace = needReplace };
            if (needReplace)
            {
                var fullname = Path.Combine(rootDir, fileName);
                var totalSize = new FileInfo(fullname).Length;
                if (chunkOffset < 0 || chunkOffset >= totalSize)
                {
                    chunkOffset = 0;
                }

                var lengthToRead = totalSize - chunkOffset;
                if (chunkSize >= 0 && chunkSize < lengthToRead)
                {
                    lengthToRead = chunkSize;
                }

                if (lengthToRead < 0)
                {
                    lengthToRead = 0;
                }

                if (lengthToRead > int.MaxValue)
                {
                    lengthToRead = int.MaxValue;
                }

                if (lengthToRead == 0)
                {
                    newFile.Hash = new byte[0];
                    newFile.Size = 0;
                }
                else
                {
                    using (var stream = File.OpenRead(fullname))
                    {
                        stream.Position = chunkOffset;
                        var bytes = new byte[(int)lengthToRead];
                        var readTotal = 0;
                        while (readTotal < bytes.Length)
                        {
                            var readNow = stream.Read(bytes, readTotal, bytes.Length - readTotal);
                            if (readNow <= 0) break;
                            readTotal += readNow;
                        }

                        if (readTotal != bytes.Length)
                        {
                            Array.Resize(ref bytes, readTotal);
                        }

                        newFile.Hash = bytes;
                        newFile.Size = readTotal;
                    }
                }

                if (chunkOffset > 0 || newFile.Size < totalSize)
                {
                    newFile.ChunkOffset = chunkOffset;
                    newFile.ChunkTotalSize = totalSize;
                }
            }
            else
            {
                newFile.Size = GetFileSize(rootDir, fileName);
            }
            return newFile;
        }

        private long GetFileSize(string rootDir, string fileName)
        {
            var fullname = Path.Combine(rootDir, fileName);
            return new FileInfo(fullname).Length;
        }
    }
}
