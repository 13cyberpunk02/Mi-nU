using MinU.Contracts;

namespace Mi_nU.Services
{
    /// <summary>
    /// Files from chats, kept on the device by their id. Links to storage change every time and expire within minutes,
    /// so the bytes are what is kept: a photo is downloaded once, however often it is shown.
    /// </summary>
    public class FileCache(ChatApi api)
    {
        // Storage needs no sign-in: the link itself is the permission.
        readonly HttpClient _download = new() { Timeout = TimeSpan.FromMinutes(5) };
        readonly Dictionary<Guid, Task<string>> _loading = [];
        readonly Lock _lock = new();

        static string Folder => Path.Combine(FileSystem.CacheDirectory, "files");

        /// <summary>Where the file is on this device, downloading it first if it isn't yet. Loads of the same file are shared.</summary>
        public Task<string> GetAsync(FileDto file)
        {
            var path = PathOf(file.Id, file.Name);
            if (File.Exists(path))
                return Task.FromResult(path);

            lock (_lock)
            {
                if (!_loading.TryGetValue(file.Id, out var loading))
                {
                    loading = DownloadAsync(file, path);
                    _loading[file.Id] = loading;
                    // Forgotten once done, so a failed download can be tried again.
                    _ = loading.ContinueWith(_ => { lock (_lock) _loading.Remove(file.Id); }, TaskScheduler.Default);
                }
                return loading;
            }
        }

        /// <summary>Keeps bytes this device already has, such as a photo it just sent, so they are never downloaded back.</summary>
        public void Put(FileDto file, byte[] bytes)
        {
            Directory.CreateDirectory(Folder);
            var path = PathOf(file.Id, file.Name);
            var temporary = path + ".part";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }

        async Task<string> DownloadAsync(FileDto file, string path)
        {
            var link = await api.GetFileLinkAsync(file.Id);
            Directory.CreateDirectory(Folder);
            // Written under another name first, so a half-downloaded file is never taken for a whole one.
            var temporary = path + ".part";
            try
            {
                using (var response = await _download.GetAsync(link.Url, HttpCompletionOption.ResponseHeadersRead))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new ApiException("Не удалось загрузить файл.");
                    await using var source = await response.Content.ReadAsStreamAsync();
                    await using var target = File.Create(temporary);
                    await source.CopyToAsync(target);
                }
                File.Move(temporary, path, overwrite: true);
                return path;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
            {
                File.Delete(temporary);
                throw new ApiException("Не удалось загрузить файл. Проверьте подключение к сети.");
            }
        }

        // The extension stays, so the system knows how to open the file.
        static string PathOf(Guid id, string name) =>
            Path.Combine(Folder, id.ToString("N") + Path.GetExtension(name).ToLowerInvariant());
    }
}
