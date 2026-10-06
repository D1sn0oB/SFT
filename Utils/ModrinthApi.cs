using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace SFTLauncher.Utils
{
    public class ModrinthApi
    {
        private readonly HttpClient _httpClient = new();
        private const string BaseUrl = "https://api.modrinth.com/v2";

        public class ModInfo
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Description { get; set; }
            public string Slug { get; set; }
            public string Author { get; set; }
            public string Version { get; set; }
            public string Category { get; set; }
            public string Downloads { get; set; }
            public string Url { get; set; }
            public List<string> GameVersions { get; set; }
            public List<string> Loader { get; set; }
        }

        /// <summary>
        /// 搜索模组
        /// </summary>
        public async Task<List<ModInfo>> SearchModsAsync(string query, string gameId = "minecraft", int limit = 20)
        {
            try
            {
                var requestUri = $"{BaseUrl}/project/search?query={Uri.EscapeDataString(query)}&game={gameId}&limit={limit}&index=relevance";
                var response = await _httpClient.GetStringAsync(requestUri);
                var mods = JsonSerializer.Deserialize<List<JsonElement>>(response);

                var result = new List<ModInfo>();
                foreach (var mod in mods)
                {
                    result.Add(new ModInfo
                    {
                        Id = mod.GetProperty("id").GetString(),
                        Title = mod.GetProperty("title").GetString(),
                        Description = mod.GetProperty("description").GetString(),
                        Slug = mod.GetProperty("slug").GetString(),
                        Author = mod.GetProperty("author").GetString(),
                        Version = mod.GetProperty("version_string").GetString(),
                        Category = mod.GetProperty("categories")[0].GetString(),
                        Downloads = mod.GetProperty("downloads").ToString(),
                        Url = $"https://modrinth.com/mod/{mod.GetProperty("slug").GetString()}",
                        GameVersions = GetJsonArray(mod, "game_versions"),
                        Loader = GetJsonArray(mod, "loaders")
                    });
                }

                return result;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Modrinth API error: {ex.Message}");
                return new List<ModInfo>();
            }
        }

        /// <summary>
        /// 获取模组详情
        /// </summary>
        public async Task<ModInfo> GetModAsync(string modId)
        {
            try
            {
                var response = await _httpClient.GetStringAsync($"{BaseUrl}/project/{modId}");
                var mod = JsonSerializer.Deserialize<JsonElement>(response);

                return new ModInfo
                {
                    Id = mod.GetProperty("id").GetString(),
                    Title = mod.GetProperty("title").GetString(),
                    Description = mod.GetProperty("description").GetString(),
                    Slug = mod.GetProperty("slug").GetString(),
                    Author = mod.GetProperty("author").GetString(),
                    Version = mod.GetProperty("version_string").GetString(),
                    Category = mod.GetProperty("categories")[0].GetString(),
                    Downloads = mod.GetProperty("downloads").ToString(),
                    Url = $"https://modrinth.com/mod/{mod.GetProperty("slug").GetString()}",
                    GameVersions = GetJsonArray(mod, "game_versions"),
                    Loader = GetJsonArray(mod, "loaders")
                };
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Modrinth API error: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 获取下载链接
        /// </summary>
        public async Task<string> GetDownloadUrlAsync(string modId, string versionId)
        {
            try
            {
                var response = await _httpClient.GetStringAsync($"{BaseUrl}/version/{versionId}");
                var version = JsonSerializer.Deserialize<JsonElement>(response);

                // 获取主文件下载地址
                var files = version.GetProperty("files");
                foreach (var file in files.EnumerateArray())
                {
                    if (file.GetProperty("primary").GetBoolean())
                    {
                        return file.GetProperty("url").GetString();
                    }
                }

                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Modrinth API error: {ex.Message}");
                return null;
            }
        }

        private List<string> GetJsonArray(JsonElement element, string propertyName)
        {
            var result = new List<string>();
            if (element.TryGetProperty(propertyName, out var property))
            {
                foreach (var item in property.EnumerateArray())
                {
                    result.Add(item.GetString());
                }
            }
            return result;
        }
    }
}
