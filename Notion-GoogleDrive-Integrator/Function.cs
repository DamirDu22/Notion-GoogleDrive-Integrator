using System.Text;
using Notion.Client;
using Newtonsoft.Json;
using Google.Apis.Drive.v3;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Functions.Worker;
using Notion_GoogleDrive_Integrator.Services;
using Notion_GoogleDrive_Integrator.Services.Exstensions;
using Microsoft.Extensions.Configuration;
using Google.Apis.Upload;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Notion_GoogleDrive_Integrator
{
    public class Function1
    {
        private readonly ILogger _logger;
        private readonly IFileService _fileService;
        private readonly INotionClient _notionClient;
        private readonly DriveService _googleClient;

        private readonly IConfiguration _configuration;


        public Function1(
            ILoggerFactory loggerFactory,
            IFileService fileService,
            INotionClient notionClient,
            DriveService googleClient,
            IConfiguration configuration
            )
        {
            _logger = loggerFactory.CreateLogger<Function1>();
            _notionClient = notionClient;
            _fileService = fileService;
            _googleClient = googleClient;
            _configuration = configuration;
        }

        [Function("GetNotionPage")]
        public async Task Run([TimerTrigger("0 0  * * *")] TimerInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
            Console.WriteLine($"C# Timer trigger function executed at: {DateTime.Now}");

            if (myTimer.ScheduleStatus is not null)
            {
                _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            try
            {
                var logString = new StringBuilder();
                
                var lastRan = GetLastRanDate(myTimer);
                
                logString.AppendLine($"Function last ran at: {lastRan}");
                
                List<Task<PaginatedList<IBlock>>> tasks = new List<Task<PaginatedList<IBlock>>>();
                List<IBlock> childBlocks = new List<IBlock>();
                List<Task<IUploadProgress>> uploads = new List<Task<IUploadProgress>>();

                var blocks = await _notionClient.Blocks.RetrieveChildrenAsync(_configuration.GetValue<string>("notion:ResourcesPageId"));
                foreach (var block in blocks.Results)
                {
                    logString.AppendLine($"Log {block.Id} last edited: {block.LastEditedTime.ToString()}");
                    if(DateTime.Compare(block.LastEditedTime, lastRan) >= 0)
                    {
                        tasks.Add(_notionClient.Blocks.RetrieveChildrenAsync(block.Id));
                    }
                }

                _logger.LogInformation(logString.ToString());

                await Task.WhenAll(tasks);
                
                foreach (var task in tasks)
                {
                    var blocksResponse = await task.ConfigureAwait(false);
                    childBlocks.AddRange(blocksResponse.Results);
                }

                var blockGroups = childBlocks.GroupBy(bl => ((PageParent)bl.Parent).PageId);

                foreach (var blockGroup in blockGroups)
                {
                    var parent = blocks.Results.Find(x => x.Id == blockGroup.Key);
                    var parentName = string.Empty;
                    var sb = new StringBuilder();

                    foreach (var block in blockGroup)
                    {
                        var textValue = block.GetText();

                        if (string.IsNullOrWhiteSpace(textValue))
                        {
                            continue;
                        }

                        sb.AppendLine(textValue);
                    }

                    if (parent != null)
                    {
                        parentName = ((ChildPageBlock)parent)?.ChildPage.Title;
                    }

                    var fileName = !string.IsNullOrEmpty(parentName) ? parentName + ".txt" : $"Orphan{DateTime.Now.ToShortDateString()}.txt";


                    await DeleteFileIfExists(fileName);
                    
                    var file = CreateFile(fileName);
                    byte[] byteArray = Encoding.UTF8.GetBytes(sb.ToString());

                    using (MemoryStream stream = new MemoryStream(byteArray))
                    {
                        var request = _googleClient.Files.Create(file, stream, "text/plain");
                        var res = await request.UploadAsync();
                    }
                    //await _fileService.WriteToFileAsync(sb.ToString(), !string.IsNullOrEmpty(parentName) ? parentName : "Orphan");
                }
            }
            catch (Exception ex)
            {
                var exceptionString = JsonConvert.SerializeObject(ex);
                _logger.LogError(ex, exceptionString);
                //await _fileService.WriteToFileAsync(exceptionString, $"Exception{DateTime.Now.Year}_{DateTime.Now.Month}_{DateTime.Now.Day}");
            }
        }

        private DateTime GetLastRanDate(TimerInfo myTimer)
        {
            //first time will be null
            var lastRan = myTimer?.ScheduleStatus?.Last ?? DateTime.UtcNow;

            //because notion sets seconds to 0
            return lastRan.AddSeconds(-(lastRan.Second + 1));
        }

        private async Task DeleteFileIfExists(string fileName)
        {
            try
            {
                var searchrequest = _googleClient.Files.List();
                searchrequest.Q = $"name:'{fileName}'";
                var existingFiles = await searchrequest.ExecuteAsync();
                List<Task<string>> tasks = new List<Task<string>>();
                if (existingFiles.Files.Any())
                {
                    foreach (var file in existingFiles.Files)
                    {
                        var deleteRequest = _googleClient.Files.Delete(file.Id);
                        tasks.Add(deleteRequest.ExecuteAsync());
                    }
                }

                await Task.WhenAll(tasks);
            }
            catch (Exception)
            {
                throw;
            }
        }


        private Google.Apis.Drive.v3.Data.File CreateFile(string fileName)
        {
            var file = new Google.Apis.Drive.v3.Data.File()
            {
                Name = fileName,
                MimeType = "text/plain",
                Parents = new List<string> { _configuration.GetValue<string>("storageAccount:folderId") }
            };

            return file;
        }
    }
}
