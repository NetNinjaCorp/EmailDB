
using EmailDB.Format;
using EmailDB.Format.FileManagement;
using EmailDB.Format.Helpers;

System.IO.File.Delete("data.blk");
var rawBlockManager = new RawBlockManager("data.blk");
var cacheManager = new CacheManager(rawBlockManager, new DefaultBlockContentSerializer());
var metadataManager = new MetadataManager(cacheManager);
var folderManager = new FolderManager(cacheManager,metadataManager);
var segmentManager = new SegmentManager(cacheManager, metadataManager);
await cacheManager.InitializeNewFile();
await folderManager.CreateFolderAsync("Inbox");
await folderManager.CreateFolderAsync("Drafts");
await folderManager.CreateFolderAsync("Sent");
await segmentManager.WriteSegmentAsync(new EmailDB.Format.Models.BlockTypes.SegmentContent()
{
    FileName = "test.txt",
    SegmentData = new byte[900],
    IsDeleted = false



});
rawBlockManager.Dispose();
rawBlockManager = new RawBlockManager("data.blk",false);
var res = await rawBlockManager.ScanFile();
Console.WriteLine(res.Count());
