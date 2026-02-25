using OCUnion;
using OCUnion.Common;
using OCUnion.Transfer.Model;
using OCUnion.Transfer.Types;
using RimWorldOnlineCity.ClientHashCheck;
using RimWorldOnlineCity.UI;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using Verse;
using RimWorldOnlineCity.Model;
using System.Text;

namespace RimWorldOnlineCity.Services
{
    sealed class ClientHashChecker : IOnlineCityClientService<bool>
    {
        public PackageType RequestTypePackage => PackageType.Request35ListFiles;
        public PackageType ResponseTypePackage => PackageType.Response36ListFiles;

        private readonly Transfer.SessionClient _sessionClient;

        public ClientHashCheckerResult Report { get; set; }

        public ClientHashChecker(Transfer.SessionClient sessionClient)
        {
            _sessionClient = sessionClient;
        }

        /// <summary>
        /// Генерируем запрос серверу в зависимости от контекста
        /// </summary>
        /// <param name="context"></param>
        /// <returns>Файлы соответствуют образцу</returns>
        public bool GenerateRequestAndDoJob(object context)
        {
            // each request - response Client-Server-Client ~100-200 ms, get all check hash in one request             
            // каждый запрос-отклик к серверу ~100-200 мс, получаем за один запрос все файлы
            // ~40 000 files *512 SHA key ~ size of package ~ 2,5 Mb 

            bool result = true;
            UpdateModsWindow.Title = "OC_Hash_Downloading".Translate();
            UpdateModsWindow.HashStatus = "";
            UpdateModsWindow.SummaryList = null;
            UpdateModsWindow.ResetProgress();
            UpdateModsWindow.SetProgress(0d, "0%");

            var clientFileChecker = (ClientFileChecker)context;
            var model = new ModelModsFilesRequest()
            {
                FolderType = clientFileChecker.Folder.FolderType,
                Files = clientFileChecker.FilesHash,
                NumberFileRequest = 0
            };
            var resumeFrom = LoadResumeOffsets(clientFileChecker.FolderPath);
            long totalSize = 0;
            long downloadSize = 0;
            try
            {
                while (true)
                {
                    model.ResumeFrom = resumeFrom.Count == 0 ? null : new Dictionary<string, long>(resumeFrom);
                    Loger.Log($"Send hash {clientFileChecker.Folder.FolderType} N{model.NumberFileRequest}");

                    var res = _sessionClient.TransObject2<ModelModsFilesResponse>(model, RequestTypePackage, ResponseTypePackage);

                    if (res.Files == null || res.Files.Count == 0)
                    {
                        if (model.NumberFileRequest == 0)
                        {
                            model.NumberFileRequest = 1;
                            continue;
                        }
                        break;
                    }

                    if (res.IgnoreTag != null && res.IgnoreTag.Count > 0)
                    {
                        //Файлы настроек присылаются каждый раз и сравнивается с текущим после удаления тэгов
                        var XMLFileName = Path.Combine(clientFileChecker.FolderPath, res.Files[0].FileName);
                        var xmlServer = FileChecker.GenerateHashXML(res.Files[0].Hash, res.IgnoreTag);
                        var xmlClient = FileChecker.GenerateHashXML(XMLFileName, res.IgnoreTag);

                        //Если хеши не равны, то продолжаем как с обычным файлом присланым для замены
                        if (xmlClient != null && xmlServer.Equals(xmlClient))
                        {
                            Loger.Log("File XML good: " + res.Files[0].FileName);
                            res.Files.RemoveAt(0);
                        }
                        else
                        {
                            Loger.Log("File XML need for a change: " + res.Files[0].FileName
                                + $" {(xmlClient?.Hash == null ? "" : Convert.ToBase64String(xmlClient.Hash).Substring(0, 6))} "
                                + $"-> {(xmlServer?.Hash == null ? "" : Convert.ToBase64String(xmlServer.Hash).Substring(0, 6))} "
                                + " withoutTag: " + res.IgnoreTag[0], Loger.LogLevel.WARNING);
                        }
                    }

                    if (res.Files.Count > 0)
                    {
                        if (totalSize == 0) totalSize = res.TotalSize;
                        downloadSize += res.Files.Sum(f => f.Size);
                        Loger.Log($"Files that need for a change: {downloadSize}/{totalSize} count={res.Files.Count}", Loger.LogLevel.WARNING);
                        string prText = "...";
                        if (totalSize > 0)
                        {
                            var progressValue = (double)downloadSize / totalSize;
                            if (progressValue < 0d) progressValue = 0d;
                            if (progressValue > 1d) progressValue = 1d;
                            var pr = (int)Math.Round(progressValue * 100d);
                            prText = pr.ToString() + "%";
                            UpdateModsWindow.SetProgress(progressValue, prText);
                        }
                        else
                        {
                            UpdateModsWindow.SetIndeterminateProgress(prText);
                        }
                        UpdateModsWindow.HashStatus = "OC_Hash_Downloading_Finish".Translate() + prText;

                        result = false;
                        if (res.Files.Any(f => f.NeedReplace))
                        {
                            var completedFiles = FileChecker.FileSynchronization(clientFileChecker.FolderPath, res, resumeFrom);
                            if (completedFiles.Count > 0)
                            {
                                clientFileChecker.RecalculateHash(completedFiles);
                            }
                        }
                        else
                        {
                            // Файлы без замены (NeedReplace=false) перезаписывать не можем,
                            // но для единообразия убираем любые stale-ключи резюма.
                            foreach (var fileName in res.Files.Select(f => f.FileName.ToLower()))
                            {
                                resumeFrom.Remove(fileName);
                            }
                        }

                        Report.FileSynchronization(res.Files);

                        var addList = res.Files
                            .Select(f => f.FileName)
                            .Where(f => f.Contains("\\"))
                            .Select(f => f.Substring(0, f.IndexOf("\\")))
                            //.Distinct() //вместо дистинкта группируем без разницы заглавных букв, но сохраняем оригинальное название
                            .Select(f => new { orig = f, comp = f.ToLower() })
                            .GroupBy(p => p.comp)
                            .Select(g => g.Max(p => p.orig))
                            .Where(f => UpdateModsWindow.SummaryList == null || !UpdateModsWindow.SummaryList.Any(sl => sl == f))
                            .ToList();
                        if (UpdateModsWindow.SummaryList == null)
                            UpdateModsWindow.SummaryList = addList;
                        else
                            UpdateModsWindow.SummaryList.AddRange(addList);
                    }

                    if (res.TotalSize == 0 //проверили весь объем
                        || (res.IgnoreTag != null && res.IgnoreTag.Count > 0) //это XML файл, они идут по одному
                        || res.Files.Any(f => !f.NeedReplace) //это файлы без права замены, а значит проблема не может быть решена
                        ) model.NumberFileRequest++;
                }

                if (result)
                {
                    UpdateModsWindow.ResetProgress();
                }
                else
                {
                    UpdateModsWindow.SetProgress(1d, "100%");
                }

                return result;
            }
            catch (Exception ex)
            {
                UpdateModsWindow.ResetProgress();
                Loger.Log(ex.ToString());
                SessionClientController.Disconnected("Error " + ex.Message);
                return false;
            }
        }

        private static Dictionary<string, long> LoadResumeOffsets(string folderPath)
        {
            var result = new Dictionary<string, long>();
            try
            {
                if (!Directory.Exists(folderPath)) return result;

                foreach (var partFile in Directory.GetFiles(folderPath, "*.ocpart", SearchOption.AllDirectories))
                {
                    var relative = partFile.Substring(folderPath.Length).TrimStart('\\', '/');
                    if (relative.EndsWith(".ocpart", StringComparison.OrdinalIgnoreCase))
                    {
                        relative = relative.Substring(0, relative.Length - ".ocpart".Length);
                    }
                    if (string.IsNullOrWhiteSpace(relative)) continue;

                    var key = relative.Replace('/', '\\').ToLower();
                    var length = new FileInfo(partFile).Length;
                    if (length > 0)
                    {
                        result[key] = length;
                    }
                }
            }
            catch (Exception ex)
            {
                Loger.Log("LoadResumeOffsets error: " + ex);
            }

            return result;
        }
    }
}
