using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using PuppeteerSharp;

namespace Hotline_Main_Parsing.common
{
    public class ManagerHotline
    {
        private static readonly TimeSpan BrowserStartTimeout = TimeSpan.FromSeconds(45);
        private static readonly TimeSpan PageStartTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan UserAgentTimeout = TimeSpan.FromSeconds(10);
        private const int PageLoadTimeoutMs = 180_000;
        private static readonly object ProcessRegistryLock = new();
        private static readonly string ProcessRegistryPath = Path.Combine(AppContext.BaseDirectory, "parser_chrome_pids.txt");
        private static readonly string BrowserProfileRoot = Path.Combine(Path.GetTempPath(), "HotlineParserChrome");
        private static readonly string[] BlockedEconomyUrlPatterns =
        {
            "*://*/*.png*", "*://*/*.jpg*", "*://*/*.jpeg*",
            "*://*/*.gif*", "*://*/*.webp*", "*://*/*.avif*",
            "*://*/*.svg*", "*://*/*.ico*",
            "*://*/*.woff*", "*://*/*.woff2*", "*://*/*.ttf*",
            "*://*/*.otf*", "*://*/*.eot*",
            "*://*/*.mp4*", "*://*/*.webm*", "*://*/*.avi*",
            "*://*/*.mov*", "*://*/*.m4v*", "*://*/*.css*",
            "*.png", "*.png?*", "*.jpg", "*.jpg?*", "*.jpeg", "*.jpeg?*",
            "*.gif", "*.gif?*", "*.webp", "*.webp?*", "*.avif", "*.avif?*",
            "*.svg", "*.svg?*", "*.ico", "*.ico?*",
            "*.woff", "*.woff?*", "*.woff2", "*.woff2?*", "*.ttf", "*.ttf?*",
            "*.otf", "*.otf?*", "*.eot", "*.eot?*",
            "*.mp4", "*.mp4?*", "*.webm", "*.webm?*", "*.avi", "*.avi?*",
            "*.mov", "*.mov?*", "*.m4v", "*.m4v?*",
            "*.css", "*.css?*", "data:image/*"
        };

        private static readonly string[] BlockedEconomyExtensions =
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".svg", ".ico",
            ".woff", ".woff2", ".ttf", ".otf", ".eot",
            ".mp4", ".webm", ".avi", ".mov", ".m4v",
            ".css"
        };

        public Browser _browser;
        public Page _page;
        public Page _secondPage;
        public string? proxy;
        public string? userAgent;
        public string? changeLink;
        private readonly bool _browserEconomyMode;
        private string? _userDataDir;
        private int? _browserProcessId;
        private bool _isClosed;

        LaunchOptions options = new LaunchOptions
        {
            Args = new[]{"--disable-gpu",
            "--disable-dev-shm-usage",
            "--disable-setuid-sandbox",
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-default-apps",
            "--disable-extensions",
            "--disable-notifications",
            "--disable-sync",
            "--no-first-run",
            "--no-sandbox",
            "--no-zygote",
            "--deterministic-fetch",
            "--disable-features=IsolateOrigins",
            "--disable-site-isolation-trials",
            "--start-maximized"},
            IgnoredDefaultArgs = new string[]
      {
                "--enable-automation"
      },
            Headless = false,
            DefaultViewport = new ViewPortOptions { Width = 1366, Height = 720 }
        };
        private Credentials _browserCredentials = null;

        public ManagerHotline()
        {
            string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            options.ExecutablePath = path;
        }

        public ManagerHotline(string proxy, string agent)
            : this(proxy, agent, false)
        {
        }

        public ManagerHotline(string proxy, string agent, bool browserEconomyMode)
        {
            _browserEconomyMode = browserEconomyMode;
            this.proxy = proxy;
            userAgent = agent;
            var proxyParts = proxy.Split("@");
            changeLink = proxyParts.Length > 2 ? proxyParts[2] : string.Empty;
            _browserCredentials = new Credentials() { Username = proxyParts[0].Split(":")[0], Password = proxyParts[0].Split(":")[1] };
            var proxyArg = $"--proxy-server={proxyParts[1]}";
            string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            options.ExecutablePath = path;
            _userDataDir = Path.Combine(BrowserProfileRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_userDataDir);
            options.UserDataDir = _userDataDir;
            var argsList = options.Args.ToList();
            argsList.Add(proxyArg);
            if (_browserEconomyMode)
            {
                argsList.Add("--blink-settings=imagesEnabled=false");
                argsList.Add("--mute-audio");
            }
            options.Args = argsList.ToArray();


            try
            {
                WaitWithTimeout(InitBrowserAsync(), BrowserStartTimeout, "запуск Chrome").GetAwaiter().GetResult();
                _browserProcessId = _browser?.Process?.Id;
                RegisterBrowserProcess(_browserProcessId, _userDataDir);
                _page = WaitWithTimeout(GetPageAsync(), PageStartTimeout, "получение вкладки Chrome").GetAwaiter().GetResult();
            }
            catch
            {
                TryKillBrowserProcess();
                throw;
            }

            try
            {
                WaitWithTimeout(_page.SetUserAgentAsync(userAgent), UserAgentTimeout, "установка User-Agent").GetAwaiter().GetResult();
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                WaitWithTimeout(
                    _page.SetUserAgentAsync("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36"),
                    UserAgentTimeout,
                    "установка запасного User-Agent").GetAwaiter().GetResult();
            }

            WaitWithTimeout(ConfigureEconomyModeAsync(), PageStartTimeout, "включение эконом-режима").GetAwaiter().GetResult();
        }

        public void SetNewProxy(string proxy)
        {
            this.proxy = proxy;
            var proxyParts = proxy.Split("@");
            _browserCredentials = new Credentials() { Username = proxyParts[0].Split(":")[0], Password = proxyParts[0].Split(":")[1] };
            var proxyArg = $"--proxy-server={proxyParts[1]}";
            changeLink = proxyParts.Length > 2 ? proxyParts[2] : string.Empty;
            string path = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
            options.ExecutablePath = path;
            var argsList = options.Args.ToList();
            argsList.Add(proxyArg);
            options.Args = argsList.ToArray();
        }
        public async Task<string> LoadHtmlPage(string url)
        {

            var response = await _page.GoToAsync(url, timeout: PageLoadTimeoutMs);
            //await BypassCloudFlare(_page);

            if (response == null)
            {
                throw new Exception("Hotline не вернул ответ по странице.");
            }


            var html = await response.TextAsync();

            if (html.Contains("Зафіксована підозріла активність"))
            {
                using var httpClient = new HttpClient();

                var content = new FormUrlEncodedContent(new Dictionary<string, string>() { { "page", url }, { "proxy", proxy } });

                var apiResponse = await httpClient.PostAsync(new Uri("http://localhost:8080"), content);
                apiResponse.EnsureSuccessStatusCode();
                //var html  = await apiResponse.Content.ReadAsStringAsync();
                html = await apiResponse.Content.ReadAsStringAsync();
            }

            return html;
        }

        public async Task CloseBrowser()
        {
            if (_isClosed) return;
            _isClosed = true;

            Process? browserProcess = null;
            try { browserProcess = _browser?.Process; } catch { }

            try
            {
                if (_browser != null)
                {
                    var closeTask = _browser.CloseAsync();
                    await Task.WhenAny(closeTask, Task.Delay(3000));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }

            KillProcessTree(browserProcess, _browserProcessId);
            UnregisterBrowserProcess(_browserProcessId);
            TryDeleteBrowserProfile(_userDataDir);
        }


        public async Task InitBrowserAsync()
        {
            _browser = (Browser)await Puppeteer.LaunchAsync(options);
        }

        public async Task<Page> GetPageAsync()
        {
            var pages = await _browser.PagesAsync();
            var page = (Page)pages.First();
            if (_browserCredentials != null)
            {
                await page.AuthenticateAsync(_browserCredentials);
            }
            return page;
        }

        public async Task<Page> GetActivePage()
        {
            return _page;
        }

        private async Task ConfigureEconomyModeAsync()
        {
            if (!_browserEconomyMode || _page == null)
            {
                return;
            }

            try
            {
                await _page.SetCacheEnabledAsync(false);
                await _page.SetBypassServiceWorkerAsync(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private async Task EnableEconomyNetworkBlockingAsync()
        {
            try
            {
                await _page.SetCacheEnabledAsync(false);
                await _page.SetBypassServiceWorkerAsync(true);

                var client = await _page.CreateCDPSessionAsync();
                await client.SendAsync("Network.enable");
                await client.SendAsync("Network.setCacheDisabled", new { cacheDisabled = true });
                await client.SendAsync("Network.clearBrowserCache");
                await client.SendAsync("Network.setBlockedURLs", new { urls = BlockedEconomyUrlPatterns });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private async Task InjectEconomyVisualFallbackAsync()
        {
            try
            {
                await _page.EvaluateFunctionOnNewDocumentAsync(
                    @"() => {
                        const apply = () => {
                            if (document.getElementById('hotline-parser-economy-style')) {
                                return;
                            }

                            const style = document.createElement('style');
                            style.id = 'hotline-parser-economy-style';
                            style.textContent = 'img,picture,video,source,canvas,svg{display:none!important;}*{background-image:none!important;}';
                            (document.head || document.documentElement).appendChild(style);
                        };

                        if (document.documentElement) {
                            apply();
                        } else {
                            document.addEventListener('DOMContentLoaded', apply, { once: true });
                        }
                    }");
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static bool ShouldBlockResource(ResourceType resourceType)
        {
            return resourceType == ResourceType.Image ||
                   resourceType == ResourceType.ImageSet ||
                   resourceType == ResourceType.Img ||
                   resourceType == ResourceType.Media ||
                   resourceType == ResourceType.Font ||
                   resourceType == ResourceType.StyleSheet;
        }

        private static bool ShouldBlockUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var cleanUrl = url.Split('?', '#')[0];
            return BlockedEconomyExtensions.Any(extension =>
                cleanUrl.EndsWith(extension, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task WaitWithTimeout(Task task, TimeSpan timeout, string operation)
        {
            var completedTask = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completedTask != task)
            {
                throw new TimeoutException($"{operation} не завершился за {timeout.TotalSeconds:0} сек.");
            }

            await task.ConfigureAwait(false);
        }

        private static async Task<T> WaitWithTimeout<T>(Task<T> task, TimeSpan timeout, string operation)
        {
            var completedTask = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            if (completedTask != task)
            {
                throw new TimeoutException($"{operation} не завершился за {timeout.TotalSeconds:0} сек.");
            }

            return await task.ConfigureAwait(false);
        }

        private void TryKillBrowserProcess()
        {
            Process? browserProcess = null;
            try { browserProcess = _browser?.Process; } catch { }

            KillProcessTree(browserProcess, _browserProcessId);
            UnregisterBrowserProcess(_browserProcessId);
            TryDeleteBrowserProfile(_userDataDir);
        }

        public static void KillTrackedBrowserProcesses()
        {
            List<(int pid, string? profilePath)> trackedProcesses;

            lock (ProcessRegistryLock)
            {
                trackedProcesses = ReadTrackedProcesses();
                TryDeleteFile(ProcessRegistryPath);
            }

            foreach (var trackedProcess in trackedProcesses)
            {
                KillProcessTree(null, trackedProcess.pid);
                TryDeleteBrowserProfile(trackedProcess.profilePath);
            }

            CleanupOldBrowserProfiles();
        }

        public static IReadOnlyList<int> GetTrackedBrowserProcessIds()
        {
            lock (ProcessRegistryLock)
            {
                return ReadTrackedProcesses()
                    .Select(item => item.pid)
                    .Distinct()
                    .ToArray();
            }
        }

        private static void RegisterBrowserProcess(int? pid, string? profilePath)
        {
            if (pid == null || pid.Value <= 0)
            {
                return;
            }

            lock (ProcessRegistryLock)
            {
                var lines = ReadTrackedProcesses()
                    .Where(item => item.pid != pid.Value)
                    .Select(item => FormatTrackedProcess(item.pid, item.profilePath))
                    .ToList();

                lines.Add(FormatTrackedProcess(pid.Value, profilePath));
                File.WriteAllLines(ProcessRegistryPath, lines);
            }
        }

        private static void UnregisterBrowserProcess(int? pid)
        {
            if (pid == null || pid.Value <= 0)
            {
                return;
            }

            lock (ProcessRegistryLock)
            {
                var lines = ReadTrackedProcesses()
                    .Where(item => item.pid != pid.Value)
                    .Select(item => FormatTrackedProcess(item.pid, item.profilePath))
                    .ToList();

                if (lines.Count == 0)
                {
                    TryDeleteFile(ProcessRegistryPath);
                    return;
                }

                File.WriteAllLines(ProcessRegistryPath, lines);
            }
        }

        private static List<(int pid, string? profilePath)> ReadTrackedProcesses()
        {
            var result = new List<(int pid, string? profilePath)>();
            if (!File.Exists(ProcessRegistryPath))
            {
                return result;
            }

            foreach (string line in File.ReadAllLines(ProcessRegistryPath))
            {
                var parts = line.Split('|');
                if (parts.Length >= 1 && int.TryParse(parts[0], out int pid) && pid > 0)
                {
                    result.Add((pid, parts.Length >= 2 ? parts[1] : null));
                }
            }

            return result;
        }

        private static string FormatTrackedProcess(int pid, string? profilePath)
        {
            return $"{pid}|{profilePath ?? string.Empty}";
        }

        private static void KillProcessTree(Process? process, int? pid)
        {
            try
            {
                process ??= pid is > 0 ? Process.GetProcessById(pid.Value) : null;
                if (process == null || process.HasExited)
                {
                    return;
                }

                if (!string.Equals(process.ProcessName, "chrome", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                process.Kill(true);
                process.WaitForExit(5000);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
            finally
            {
                process?.Dispose();
            }
        }

        private static void TryDeleteBrowserProfile(string? profilePath)
        {
            if (string.IsNullOrWhiteSpace(profilePath))
            {
                return;
            }

            try
            {
                string fullProfilePath = Path.GetFullPath(profilePath);
                string fullProfileRoot = Path.GetFullPath(BrowserProfileRoot);
                if (!fullProfilePath.StartsWith(fullProfileRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (Directory.Exists(fullProfilePath))
                {
                    Directory.Delete(fullProfilePath, true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void CleanupOldBrowserProfiles()
        {
            try
            {
                if (!Directory.Exists(BrowserProfileRoot))
                {
                    return;
                }

                foreach (string profilePath in Directory.GetDirectories(BrowserProfileRoot))
                {
                    try
                    {
                        var directory = new DirectoryInfo(profilePath);
                        if (directory.LastWriteTimeUtc < DateTime.UtcNow.AddHours(-12))
                        {
                            Directory.Delete(profilePath, true);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }
    }
}
