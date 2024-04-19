using System.Text;
using Notion.Client;
using Newtonsoft.Json;
using Google.Apis.Drive.v3;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Google.Apis.Upload;
using static System.Runtime.InteropServices.JavaScript.JSType;
using Notion_GoogleDrive_Integrator.Entities;
using Azure.Data.Tables;
using Google.Apis.Drive.v3.Data;

namespace Notion_GoogleDrive_Integrator
{
    public class Function1
    {
        private readonly ILogger _logger;
        private readonly INotionClient _notionClient;
        private readonly DriveService _googleClient;

        private readonly IConfiguration _configuration;

        private readonly TableServiceClient _tableServiceCleint;
        private readonly TableClient _tableClient;

        public Function1(
            ILoggerFactory loggerFactory,
            INotionClient notionClient,
            DriveService googleClient,
            IConfiguration configuration
            )
        {
            _logger = loggerFactory.CreateLogger<Function1>();
            _notionClient = notionClient;
            _googleClient = googleClient;
            _configuration = configuration;

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

                List<PageDto> blocksToProcessNew = GetModifiedTableStorageBlocks(tableStorageBlocks, notionBlocks.Results, blocksPartitionKey);


                List<Task<PaginatedList<IBlock>>> tasks = new List<Task<PaginatedList<IBlock>>>();
                foreach (var blockDto in blocksToProcessNew)
                {
                    logString.AppendLine($"Log {blockDto.NotionBlock.Id}");
                    logString.Append($"last edited: {blockDto.NotionBlock.LastEditedTime}");
                    tasks.Add(_notionClient.Blocks.RetrieveChildrenAsync(blockDto.NotionBlock.Id));
                }

                await Task.WhenAll(tasks);

                if (string.IsNullOrWhiteSpace(logString.ToString()))
                {
                    _logger.LogInformation(logString.ToString());
                }

                List<IBlock> childBlocks = new List<IBlock>();
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

                    int filesDeleted = await DeleteFileIfExists(existingFiles.Files);
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
                var azureResponses = await SaveBlocksToTableStorage(blocksToProcessNew!);

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
                if (task.IsCompletedSuccessfully)
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
        /// <param name="pages"></param>
        /// <returns></returns>
        /// <remarks>TODO: this shold not depend on TSBlock but on DTO</remarks>
        private async Task<List<Azure.Response>> SaveBlocksToTableStorage(List<PageDto> pages)
        {
            if (pages == null || !pages.Any()) return new List<Azure.Response>();

            List<Task<Azure.Response>> tasks = new List<Task<Azure.Response>>();
            foreach (var item in pages)
            {
                switch (item.Action)
                {
                    case BlockAction.Create:
                        tasks.Add(_tableClient.UpdateEntityAsync(item.TsBlock, item.TsBlock.ETag));
                        break;
                    case BlockAction.Update:
                        tasks.Add(_tableClient.AddEntityAsync(item.TsBlock));
                        break;
                }
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
        /// NEW
        /// Return all blocks from table storage that need to be updated/created by comparing them to current blocks from Notion
        /// </summary>
        /// <param name="tsBlocks"></param>
        /// <param name="blocks"></param>
        private List<PageDto> GetModifiedTableStorageBlocks(List<TSBlock> tsBlocks, List<IBlock> blocks, string partitionKey)
        {
            List<PageDto> blocksToProcess = new List<PageDto>();

            foreach(var block in blocks)
            {
                var existingInTableStorgae = tsBlocks.FirstOrDefault(x => x.BlockId == block.Id);

                if(existingInTableStorgae != null)
                {
                    if(existingInTableStorgae.EditDate < block.LastEditedTime)
                    {
                        existingInTableStorgae.EditDate = block.LastEditedTime;
                        blocksToProcess.Add(new PageDto
                        {
                            TsBlock = existingInTableStorgae,
                            NotionBlock = block,
                            Action = BlockAction.Update
                        });
                    }
                }
                else
                {
                    TSBlock tsBlock = new TSBlock
                    {
                        PartitionKey = partitionKey,
                        BlockId = block.Id,
                        EditDate = block.LastEditedTime,
                        RowKey = block.Id
                    };

                    blocksToProcess.Add(new PageDto
                    {
                        TsBlock = tsBlock,
                        NotionBlock = block,
                        Action = BlockAction.Create
                    });
                }
            }

            return blocksToProcess;
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
