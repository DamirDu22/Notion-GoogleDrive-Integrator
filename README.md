# Notion-GoogleDrive-Integrator

A function with simple functionality but not so simple implementation :D

This function takes all pages from the designated Notion workspace and copies them to the desired Google Drive folder.

The function accesses notion using Notion Client Library and uses an API key generated on Notion site.
Google is accessed using Google Drive Api Client Library and uses Google Cloud Service Accounts. The Json file containing the private key is downloaded upon creation and should, for now, be stored in Azure Storage Account File Share. (It is not recommended/safe to use Storage Account for this and the file should be kept in Azure service designed to hold secrets. This will be updated.)

Application settings are all stored in Environment variables in the Function. (this will be changed later to store in another service designed for this)
The function is not created automatically so it needs to be created in Azure and application settings need to be added manually.

Application settings:

"environmentVariables": {
  "notion:NotionBaseUrl": "https://api.notion.com/v1/",
  "notion:NotionSecret": "<<NOTION_API_KEY>>",
  "notion:NotionVersion": "2022-06-28",
  "notion:ResourcesPageId": "<<YOUR_NOTION_WORKSPACE_ID>>", (get the id by searching the worskpace/page by name)
  "storageAccount:fileName": "<<NAME_OF_THE_PRIVATE_KEY_FILE>>",
  "storageAccount:shareName": "<<FILE_SHARE_NAME>>",
  "storageAccount:directoryName": "<<DIRECTORY_NAME>>",
  "storageAccount:folderId": "<<ID_OF_GOOGLE_FOLDER_WHERE_PAGES_ARE_STORED>>", (wrongly named for now)
  "storageAccount:blocksTableName": "BLOCK_METADATA_TABLE",
  "storageAccount:blocksTablePartitionKey": "<<YOUR_PARTITION>>",
  "ConnectionStrings:storageAccount": "<<STORAGE_ACCOUNT_CONNECTION_STRING>>"
  //}
