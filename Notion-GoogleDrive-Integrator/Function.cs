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
using System.Collections.Concurrent;
using Google.Apis.Drive.v3.Data;
using System.Reflection.Metadata.Ecma335;
using System.Collections.Generic;

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
        public async Task Run([TimerTrigger("0 0 0/24 * * *")] TimerInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
            Console.WriteLine($"C# Timer trigger function executed at: {DateTime.Now}");

            if (myTimer.ScheduleStatus is not null)
            {
                _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            try
            {
                var blocksPartitionKey = _configuration.GetValue<string>("storageAccount:blocksTablePartitionKey");

                var logString = new StringBuilder();
                
                
                List<Task<IUploadProgress>> uploads = new List<Task<IUploadProgress>>();

                var notionBlocks = await _notionClient.Blocks.RetrieveChildrenAsync(_configuration.GetValue<string>("notion:ResourcesPageId"));

                //Get blocks from table storage
                var tableStorageBlocks = _tableClient.Query<TSBlock>(x => x.PartitionKey == blocksPartitionKey).ToList();

                IEnumerable<TSBlock?> modifiedBlocks = GetModifiedTableStorageBlocks(tableStorageBlocks, notionBlocks.Results);
                IEnumerable<TSBlock?> newBlocks = GetNewTableStorageBlocks(tableStorageBlocks, notionBlocks.Results, blocksPartitionKey);
                IEnumerable<TSBlock?> blocksToProcess = modifiedBlocks.Concat(newBlocks);

                List<Task<PaginatedList<IBlock>>> tasks = new List<Task<PaginatedList<IBlock>>>();
                List<IBlock> childBlocks = new List<IBlock>();
                foreach (var block in blocksToProcess)
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

                var blockGroupsByParentId = childBlocks.GroupBy(cb => ((PageParent)cb.Parent).PageId);

                foreach (var blockGroup in blockGroupsByParentId)
                {
                    var parent = notionBlocks.Results.Find(x => x.Id == blockGroup.Key);
                    var parentName = ((ChildPageBlock?)parent)?.ChildPage?.Title;
                    if (string.IsNullOrEmpty(parentName))
                    {
                        continue;
                    }

                    var pageContent = new StringBuilder();

                    foreach (var block in blockGroup)
                    {
                        var textValue = GetText(block);

                        if (string.IsNullOrWhiteSpace(textValue))
                        {
                            continue;
                        }

                        pageContent.AppendLine(textValue);
                    }

                    var fileName = $"{parentName}.txt";

                    var searchrequest = _googleClient.Files.List();
                    searchrequest.Q = $"name:'{fileName}'";
                    FileList existingFiles = await searchrequest.ExecuteAsync();

                    var filesDeleted = await DeleteFileIfExists(existingFiles.Files);
                    //do we need to retry failed deletions?

                    var googleFolderIds = new List<string> { _configuration.GetValue<string>("google:drive:folderId") };

                    var file = CreateFile(fileName, googleFolderIds);

                    byte[] byteArray = Encoding.UTF8.GetBytes(pageContent.ToString());

                    using (MemoryStream stream = new MemoryStream(byteArray))
                    {
                        var request = _googleClient.Files.Create(file, stream, "text/plain");
                        var res = await request.UploadAsync();
                    }
                }

                //insert/update to table storage
                var modifyResponses = await UpdateBlocksInTableStorageNew(modifiedBlocks!);
                var saveResponses = await SaveBlocksToTableStorage(newBlocks!);

                //TODO: handle failed requests to table storage

            }
            catch (Exception ex)
            {
                var exceptionString = JsonConvert.SerializeObject(ex);
                _logger.LogError(ex, exceptionString);
            }
        }

        /// <summary>
        /// Delete files from google drive that match search by Name
        /// </summary>
        /// <param name="fileName"></param>
        /// <returns></returns>
        /// <remarks></remarks>
        private async Task<int> DeleteFileIfExists(IList<Google.Apis.Drive.v3.Data.File> existingFiles)
        {
            int count = 0;
            if (!existingFiles.Any())
            {
                return count;
            }

            List<Task<string>> tasks = new List<Task<string>>();
            foreach (var file in existingFiles)
            {
                var deleteRequest = _googleClient.Files.Delete(file.Id);
                tasks.Add(deleteRequest.ExecuteAsync());
            }

            await Task.WhenAll(tasks);

            foreach(var task in tasks)
            {
                bool taskCompleted = task.IsCompletedSuccessfully;
                if (taskCompleted)
                {
                    count++;
                }
            }

            return count;
        }

        /// <summary>
        /// Build file for Google drive
        /// </summary>
        /// <param name="fileName"></param>
        /// <param name="partents"></param>
        /// <param name="mimeType"></param>
        /// <returns></returns>
        private Google.Apis.Drive.v3.Data.File CreateFile(string fileName, List<string> partents, string mimeType = "text/plain")
        {
            var file = new Google.Apis.Drive.v3.Data.File()
            {
                Name = fileName,
                MimeType = mimeType,
                Parents = partents
            };

            return file;
        }

        /// <summary>
        /// Add new Notion block metadata to table storage
        /// </summary>
        /// <param name="blocks"></param>
        /// <returns></returns>
        /// <remarks>TODO: this shold not depend on TSBlock but on DTO</remarks>
        private async Task<List<Azure.Response>> SaveBlocksToTableStorage(IEnumerable<TSBlock> blocks)
        {
            if (blocks == null || !blocks.Any()) return new List<Azure.Response>();

            List<Task<Azure.Response>> tasks = new List<Task<Azure.Response>>();
            List<TSBlock> failedSaves = new List<TSBlock>();
            foreach (var item in blocks)
            {
                tasks.Add(_tableClient.AddEntityAsync<TSBlock>(item));
            }

            await Task.WhenAll(tasks);

            List<Azure.Response> responses = new List<Azure.Response>();
            foreach (var task in tasks)
            {
                responses.Add(await task);
            }

            return responses;
        }

        /// <summary>
        /// Update Notion block metadata in table storage
        /// </summary>
        /// <param name="blocks"></param>
        /// <returns></returns>
        /// <remarks>TODO: this shold not depend on TSBlock but on DTO</remarks>
        private async Task<List<Azure.Response>> UpdateBlocksInTableStorageNew(IEnumerable<TSBlock> blocks)
        {
            if (blocks == null || !blocks.Any()) return new List<Azure.Response>();

            List<Task<Azure.Response>> tasks = new List<Task<Azure.Response>>();
            foreach (var item in blocks)
            {
                tasks.Add(_tableClient.UpdateEntityAsync<TSBlock>(item, item.ETag));
            }

            await Task.WhenAll(tasks);

            List < Azure.Response > responses = new List<Azure.Response>();
            foreach (var task in tasks)
            {
                responses.Add(await task);
            }

            return responses;
        }

        /// <summary>
        /// Return all blocks from table storage that have been modified by comparing them to current blocks from Notion
        /// </summary>
        /// <param name="tsBlocks"></param>
        /// <param name="blocks"></param>
        private IEnumerable<TSBlock> GetModifiedTableStorageBlocks(IEnumerable<TSBlock> tsBlocks, List<IBlock> blocks)
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

        /// <summary>
        /// Return blocks that will be created in google drive
        /// </summary>
        /// <param name="tsBlocks"></param>
        /// <param name="blocks"></param>
        /// <param name="partitionKey"></param>
        /// <returns></returns>
        private IEnumerable<TSBlock> GetNewTableStorageBlocks(IEnumerable<TSBlock> tsBlocks, List<IBlock> blocks, string partitionKey)
        {
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
                var existing = tsBlocks.ToList().Find(tsb => tsb.BlockId == item.Id);
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

        /// <summary>
        /// Method that extracts text from Notion blocks.
        /// TODO: add more cases: url, header, etc
        /// </summary>
        /// <param name="block"></param>
        /// <returns></returns>
        public string GetText(IBlock block)
        {
            switch (block.Type)
            {
                case BlockType.Paragraph:
                    return ((ParagraphBlock)block)?.Paragraph?.RichText?.FirstOrDefault()?.PlainText ?? "";
                case BlockType.BulletedListItem:
                    return $"-{((BulletedListItemBlock)block)?.BulletedListItem?.RichText?.FirstOrDefault()?.PlainText}";
                case BlockType.NumberedListItem:
                    return $"-{((NumberedListItemBlock)block)?.NumberedListItem?.RichText?.FirstOrDefault()?.PlainText}";
                default:
                    return "";
            }
        }
    }
}
