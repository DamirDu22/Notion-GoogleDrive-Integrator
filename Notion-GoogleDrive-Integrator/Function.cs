using System;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace Notion_GoogleDrive_Integrator
{
    public class Function1
    {
        private readonly ILogger _logger;

        private static readonly string NotionBaseUrl = "https://api.notion.com/v1/";
        private static readonly string NotionSecret = "secret_rN7QWgc7CY5pGWx7rcFSq525uYqPomdYkGjPpETxFTl";
        private static readonly string NotionVersionHeaderName = "Notion-Version";
        private static readonly string NotionVersionHeaderValue = "2022-06-28";
        private static readonly string ResourcesPageId = "10d4830e-d02b-4317-9a38-e51f01216555";

        public Function1(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory.CreateLogger<Function1>();
        }

        [Function("GetNotionPage")]
        public async Task Run([TimerTrigger("0/5 * * * *")] TimerInfo myTimer)
        {
            _logger.LogInformation($"C# Timer trigger function executed at: {DateTime.Now}");
            Console.WriteLine($"C# Timer trigger function executed at: {DateTime.Now}");

            if (myTimer.ScheduleStatus is not null)
            {
                _logger.LogInformation($"Next timer schedule at: {myTimer.ScheduleStatus.Next}");
            }

            NotionClient.DefaultRequestHeaders.Add(NotionVersionHeaderName, NotionVersionHeaderValue);
            NotionClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {NotionSecret}");

            var result = await NotionClient.GetAsync($"pages/{ResourcesPageId}");

            result.EnsureSuccessStatusCode()
                .WriteToConsole();
        }

        private static readonly HttpClient NotionClient = new()
        {
            BaseAddress = new Uri(NotionBaseUrl)
        };
    }
}
