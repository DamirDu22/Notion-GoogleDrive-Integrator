using Azure.Storage.Files.Shares;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Notion.Client;
using Notion_GoogleDrive_Integrator.Entities.Configuration;
using Notion_GoogleDrive_Integrator.Services;

var hostBuilder = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();
    });
    //.ConfigureAppConfiguration(configuration =>
    //{
    //    configuration.AddJsonFile("local.settings.json", optional: true, reloadOnChange: true);
    //}).ConfigureAppConfiguration(configuration =>
    //{
    //    configuration.AddJsonFile("settings.json", optional: true, reloadOnChange: true);
    //});

hostBuilder.ConfigureServices((context, services) =>
{
    var configurationRoot = context.Configuration;
    services.Configure<NotionConfiguration>(configurationRoot.GetSection("notion"));

    //setup notion client
    var notionBaseUrl = configurationRoot.GetValue<string>("notion:NotionBaseUrl");
    var notionAuthToken = configurationRoot.GetValue<string>("notion:NotionSecret");
    var notionVersion = configurationRoot.GetValue<string>("notion:NotionVersion");

    services.AddSingleton<INotionClient>(x => NotionClientFactory.Create(new ClientOptions
    {
        BaseUrl = notionBaseUrl,
        AuthToken = notionAuthToken,
        NotionVersion = notionVersion
    }));

    //setup Google client
    var storageConnStr = configurationRoot.GetValue<string>("ConnectionStrings:storageAccount");
    var shareName = configurationRoot.GetValue<string>("storageAccount:shareName");
    var directoryName = configurationRoot.GetValue<string>("storageAccount:directoryName");
    var fileName = configurationRoot.GetValue<string>("storageAccount:fileName");

    ShareClient share = new ShareClient(storageConnStr, shareName);
    ShareDirectoryClient directory = share.GetDirectoryClient(directoryName);
    ShareFileClient file = directory.GetFileClient(fileName);
    var fil = file.Download();

    string[] Scopes = { DriveService.Scope.Drive };
    string ApplicationName = "NotionGoogleDriveIntegrator";
    GoogleCredential credential;
    //GetApplicationDefault will look for GOOGLE_APPLICATION_CREDENTIALS set in launchSettings.json
    using (Stream stream = fil.Value.Content)
    {
        credential = GoogleCredential.FromStream(stream).CreateScoped(Scopes);
    }

    services.AddSingleton(x => new DriveService(new BaseClientService.Initializer()
    {
        HttpClientInitializer = credential,
        ApplicationName = ApplicationName,

    }));

    services.AddSingleton<IFileService, FileService>();
});

var host = hostBuilder.Build(); 

host.Run();
