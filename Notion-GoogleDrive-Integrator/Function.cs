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
using Notion_GoogleDrive_Integrator.Entities;
using Azure.Data.Tables;
using Microsoft.Extensions.Caching.Memory;

namespace Notion_GoogleDrive_Integrator
{
    public class Function1
    {
        private readonly ILogger _logger;
        private readonly IFileService _fileService;
        private readonly INotionClient _notionClient;
        private readonly DriveService _googleClient;

        private readonly IConfiguration _configuration;

        private readonly TableServiceClient _tableServiceCleint;
        private readonly TableClient _tableClient;

        private readonly IMemoryCache _cache;

        public Function1(
            ILoggerFactory loggerFactory,
            IFileService fileService,
            INotionClient notionClient,
            DriveService googleClient,
            IConfiguration configuration,
            IMemoryCache cache
            )
        {
            _logger = loggerFactory.CreateLogger<Function1>();
            _notionClient = notionClient;
            _fileService = fileService;
            _googleClient = googleClient;
            _configuration = configuration;
            _cache = cache;

            _tableServiceCleint = new TableServiceClient(_configuration.GetValue<string>("ConnectionStrings:storageAccount"));
            _tableClient = _tableServiceCleint.GetTableClient(
                    tableName: _configuration.GetValue<string>("storageAccount:blockstableName")
            );
        }

        [Function("GetNotionPage")]
        public async Task Run([TimerTrigger("0 0 0/24 * *")] TimerInfo myTimer)
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
                
                List<Task<PaginatedList<IBlock>>> tasks = new List<Task<PaginatedList<IBlock>>>();
                List<IBlock> childBlocks = new List<IBlock>();
                List<Task<IUploadProgress>> uploads = new List<Task<IUploadProgress>>();

                var blocks = await _notionClient.Blocks.RetrieveChildrenAsync(_configuration.GetValue<string>("notion:ResourcesPageId"));

                //Get blocks from table storage
                var tsBlocks = GetBlocksFromTableStorage();

                //get modified blocks
                var modifiedBlocks = GetModifiedTSBlocks(tsBlocks, blocks.Results);

                //get new blocks
                var newBlocks = GetNewTSBlocks(tsBlocks.ToList(), blocks.Results);

                var allBlocks = new List<TSBlock>();
                allBlocks.AddRange(modifiedBlocks);
                allBlocks.AddRange(newBlocks);

                foreach (var block in allBlocks)
                {
                    logString.AppendLine($"Log {block.BlockId} last edited: {block.EditDate.ToString()}");
                    tasks.Add(_notionClient.Blocks.RetrieveChildrenAsync(block.BlockId));
                }

                await Task.WhenAll(tasks);

                if (string.IsNullOrWhiteSpace(logString.ToString()))
                {
                    _logger.LogInformation(logString.ToString());
                }

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

                //update/insert into table storage
                await UpdateBlocksInTableStorage(modifiedBlocks);
                await SaveBlocksToTableStorage(newBlocks);

            }
            catch (Exception ex)
            {
                var exceptionString = JsonConvert.SerializeObject(ex);
                _logger.LogError(ex, exceptionString);
                //await _fileService.WriteToFileAsync(exceptionString, $"Exception{DateTime.Now.Year}_{DateTime.Now.Month}_{DateTime.Now.Day}");
            }
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

        private IEnumerable<TSBlock> GetBlocksFromTableStorage()
        {
            var key = _configuration.GetValue<string>("storageAccount:blocksTablePartitionKey");
            var notionBlocks = _tableClient.Query<TSBlock>(x => x.PartitionKey == key).ToList();
            return notionBlocks;
        }

        private IEnumerable<TSBlock> GetBlocksFromTableStorageWithCache()
        {
            var key = _configuration.GetValue<string>("storageAccount:blocksTablePartitionKey");
            List<TSBlock> notionBlocks = new List<TSBlock>();
            notionBlocks = _cache.Get<List<TSBlock>>(key);

            if(notionBlocks == null || !notionBlocks.Any()) 
            { 
                notionBlocks = _tableClient.Query<TSBlock>(x => x.PartitionKey == key).ToList();
                _cache.Set(key, notionBlocks, new DateTimeOffset(DateTime.Now.AddDays(1)));
            }

            return notionBlocks;
        }

        private async Task SaveBlocksToTableStorage(IEnumerable<TSBlock> blocks)
        {
            if (blocks == null || !blocks.Any()) return;

            List<Task<Azure.Response>> tasks = new List<Task<Azure.Response>>();
            foreach(var item in blocks)
            {
                tasks.Add(_tableClient.AddEntityAsync<TSBlock>(item));
            }

            await Task.WhenAll(tasks);
        }

        private async Task UpdateBlocksInTableStorage(IEnumerable<TSBlock> blocks)
        {
            if(blocks == null || !blocks.Any()) return;

            List<Task<Azure.Response>> tasks = new List<Task<Azure.Response>>();
            foreach (var item in blocks)
            {
                tasks.Add(_tableClient.UpdateEntityAsync<TSBlock>(item, item.ETag));
            }

            await Task.WhenAll(tasks);
        }

        private IEnumerable<IBlock> GetModifiedBlocks(IEnumerable<TSBlock> tsBlocks, List<IBlock> blocks)
        {
            List<IBlock> modifiedBlocks = new List<IBlock>();

            foreach (var item in blocks)
            {
                var modified = tsBlocks.Any(tsb => tsb.BlockId == item.Id && tsb.EditDate > item.LastEditedTime);
                if (modified)
                {
                    modifiedBlocks.Add(item);
                }
            }
            
            return modifiedBlocks;
        }

        private IEnumerable<TSBlock> GetModifiedTSBlocks(IEnumerable<TSBlock> tsBlocks, List<IBlock> blocks)
        {
            List<TSBlock> modifiedBlocks = new List<TSBlock>();

            foreach (var tsb in tsBlocks)
            {
                var modified = blocks.Any(item => item.Id == tsb.BlockId && item.LastEditedTime > tsb.EditDate);
                if (modified)
                {
                    modifiedBlocks.Add(tsb);
                }
            }

            return modifiedBlocks;
        }

        private IEnumerable<IBlock> GetNewBlocks(IEnumerable<TSBlock> tsBlocks, List<IBlock> blocks)
        {
            List<IBlock> modifiedBlocks = new List<IBlock>();

            foreach (var item in blocks)
            {
                var modified = tsBlocks.Any(tsb => tsb.BlockId != item.Id);
                if (modified)
                {
                    modifiedBlocks.Add(item);
                }
            }

            return modifiedBlocks;
        }

        private IEnumerable<TSBlock> GetNewTSBlocks(List<TSBlock> tsBlocks, List<IBlock> blocks)
        {
            var partitionKey = _configuration.GetValue<string>("storageAccount:blocksTablePartitionKey");
            List<TSBlock> newBlocks = new List<TSBlock>();

            if (!tsBlocks.Any() && blocks.Any())
            {
                return blocks.Select(x =>
                {
                    return new TSBlock
                    {
                        PartitionKey = partitionKey,
                        RowKey = x.Id,
                        EditDate = x.LastEditedTime,
                        BlockId = x.Id
                    };
                });
            }

            foreach (var item in blocks)
            {
                var existing = tsBlocks.Find(tsb => tsb.BlockId == item.Id);
                if (existing == null)
                {
                    newBlocks.Add(new TSBlock
                    {
                        PartitionKey = partitionKey,
                        RowKey = item.Id,
                        EditDate = item.LastEditedTime,
                        BlockId = item.Id
                    });
                }
            }

            return newBlocks;
        }
    }
}
