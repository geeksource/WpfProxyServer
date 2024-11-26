using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Policy;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Newtonsoft.Json;
using WpfProxyServer;

public class ProxyServer
{
    private readonly HttpListener _httpListener;
    private readonly CookieContainer _cookieContainer;
    private readonly HttpClient _httpClient;
    private readonly List<BundleConfig> _bundles;
    private readonly List<string> _virtualUrls;
    public event Action<string> LogMessage;

    private readonly string targetRootUrl;
    private readonly string prefix;

    private readonly DatabaseLogger _databaseLogger;

    public ProxyServer(string _prefix,string _targetRootUrl)
    {
        prefix = _prefix;
        targetRootUrl= _targetRootUrl;

        _httpListener = new HttpListener();
        _cookieContainer = new CookieContainer();
        _httpClient = new HttpClient(new HttpClientHandler { CookieContainer = _cookieContainer });
        _bundles = LoadBundlesFromConfig();
        _virtualUrls = LoadVirtualUrlsFromConfig();

        _databaseLogger = new DatabaseLogger("Data Source=PostRequests.db;Version=3;");

    }

    public async Task StartAsync()
    {
        _httpListener.Prefixes.Add(prefix);
        _httpListener.Start();

        LogMessage?.Invoke($"Proxy server started at {prefix}, forwarding to {targetRootUrl}");

        while (_httpListener.IsListening)
        {
            var context = await _httpListener.GetContextAsync();
            await ProcessRequestAsync(context, targetRootUrl);
        }
    }

    public void Stop()
    {
        _httpListener.Stop();
        LogMessage?.Invoke("Proxy server stopped.");
    }

    private List<BundleConfig> LoadBundlesFromConfig()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var bundlesSection = config.GetSection("Bundles").Get<List<BundleConfig>>();

        bundlesSection?.ForEach(item => item.Name = item.Name.ToLower().Replace("~", ""));
        if(bundlesSection!=null)
            return bundlesSection;
        return new();
    }
    private List<string> LoadVirtualUrlsFromConfig()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .Build();

        var virtualUrls = config.GetSection("VirtualUrls").Get<List<string>>();

        virtualUrls?.ForEach(item => item = item.ToLower().Replace("~", ""));
        if (virtualUrls != null)
            return virtualUrls;
        return new();
    }
    private Stream CloneNonSeekableStream(Stream inputStream)
    {
        // Create a MemoryStream to hold the content
        MemoryStream clonedStream = new MemoryStream();

        // Copy the content of the non-seekable stream to the MemoryStream
        inputStream.CopyTo(clonedStream);
        // Reset the position of the cloned stream for further use
        clonedStream.Position = 0;

        return clonedStream;
    }

    private async Task ProcessRequestAsync(HttpListenerContext context, string targetRootUrl)
    {
        var request = context.Request;
        try
        {
            // Check if the request matches any configured bundle
            string AbsolutePath = request.Url.AbsolutePath.ToLower();
            var bundle = _bundles.FirstOrDefault(b => AbsolutePath.Contains(b.Name));
            if (bundle != null)
            {
                // If the bundle is for CSS files
                if (bundle.Name.Contains("themes") || bundle.Name.Contains("mystyles"))
                {
                    await HandleCssBundleRequest(context, bundle);
                }
                // If the bundle is for JS files
                else if (bundle.Name.Contains("scripts"))
                {
                    await HandleJsBundleRequest(context, bundle);
                }
                return;
            }

            // Check if the request is for an image (e.g., captcha)
            if (request.Url.AbsolutePath.Contains("DefaultCaptcha/Generate"))
            {
                await HandleImageRequest(context, targetRootUrl);
                return;
            }

            // Process the regular request if it's not a bundled request
            string targetUrlString = targetRootUrl.TrimEnd('/') + request.Url.AbsolutePath;
            if (!string.IsNullOrEmpty(request.Url.Query))
            {
                targetUrlString += Uri.EscapeUriString(request.Url.Query); // Ensure query parameters are URL-encoded
            }

            Uri targetUri = new Uri(targetUrlString);

            LogMessage?.Invoke($"Processing request: {request.HttpMethod} {request.Url}");
            LogMessage?.Invoke($"Target URL: {targetUri.AbsoluteUri}");

            // Forward the request to the target server
            var targetRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri);

            // Forward request body if present
            if (request.HasEntityBody)
            {
                using var bodyReader = new StreamReader(request.InputStream, request.ContentEncoding);
                var bodyContent = await bodyReader.ReadToEndAsync();
                targetRequest.Content = new StringContent(bodyContent, Encoding.UTF8, request.ContentType);
            }

            // Forward headers
            foreach (string header in request.Headers)
            {
                if (!WebHeaderCollection.IsRestricted(header))
                {
                    targetRequest.Headers.TryAddWithoutValidation(header, request.Headers[header]);
                }
            }

            #region Logging Request
            if(targetRequest.Method == HttpMethod.Post)
            {
                string json = JsonConvert.SerializeObject(targetRequest);
                _databaseLogger.LogPostRequest(targetUrlString, "", json);
            }
           
            #endregion

            using var response = await _httpClient.SendAsync(targetRequest, HttpCompletionOption.ResponseHeadersRead);
            var responseStream = await response.Content.ReadAsStreamAsync();

            // Handle gzipped content
            Stream processedStream = response.Content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(responseStream, CompressionMode.Decompress)
                : responseStream;

            // Prepare the response to the browser
            using var memoryStream = new MemoryStream();
            await processedStream.CopyToAsync(memoryStream);
            byte[] responseBody = memoryStream.ToArray();

            // Set response headers
            context.Response.ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = string.Join(",", header.Value);
            }

            // Forward Set-Cookie headers back to the browser to maintain session
            if (response.Headers.Contains("Set-Cookie"))
            {
                foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
                {
                    context.Response.Headers.Add("Set-Cookie", cookie);
                }
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentLength64 = responseBody.Length;
            await context.Response.OutputStream.WriteAsync(responseBody, 0, responseBody.Length);

            LogMessage?.Invoke($"Request completed: {request.Url}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error processing request: {ex.Message}");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            byte[] errorMessage = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
            await context.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }

    private async Task ProcessRequestAsync1(HttpListenerContext context, string targetRootUrl)
    {
        var request = context.Request;
        try
        {
             //mystyles    ->
            // Check if the request matches any configured bundle
            string AbsolutePath =  request.Url.AbsolutePath.ToLower();
            var bundle = _bundles.FirstOrDefault(b => AbsolutePath.Contains(b.Name));
            if (bundle != null)
            {
                // If the bundle is for CSS files
                if (bundle.Name.Contains("themes") || bundle.Name.Contains("mystyles"))
                {
                    await HandleCssBundleRequest(context, bundle);
                }
                // If the bundle is for JS files
                else if (bundle.Name.Contains("scripts"))
                {
                    await HandleJsBundleRequest(context, bundle);
                }
                return;
            }

            var virtualUrl = _virtualUrls.FirstOrDefault(b => AbsolutePath.Contains(AbsolutePath.ToLower()));
            if (virtualUrl != null)
            {
                // Handle virtual URL requests (captcha or others)
                await HandleImageRequest(context, targetRootUrl);
                return;
            }

            // Process the regular request if it's not a bundled request
            string targetUrlString = targetRootUrl.TrimEnd('/') + request.Url.AbsolutePath;
            if (!string.IsNullOrEmpty(request.Url.Query))
            {
                targetUrlString += Uri.EscapeUriString(request.Url.Query); // Ensure query parameters are URL-encoded
            }

            Uri targetUri = new Uri(targetUrlString);

            LogMessage?.Invoke($"Processing request: {request.HttpMethod} {request.Url}");
            LogMessage?.Invoke($"Target URL: {targetUri.AbsoluteUri}");

            // Forward the request to the target server
            var targetRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUri);

            // Forward request body if present
            if (request.HasEntityBody)
            {
                using var bodyReader = new StreamReader(request.InputStream, request.ContentEncoding);
                var bodyContent = await bodyReader.ReadToEndAsync();
                targetRequest.Content = new StringContent(bodyContent, Encoding.UTF8, request.ContentType);
            }

            // Forward headers
            foreach (string header in request.Headers)
            {
                if (!WebHeaderCollection.IsRestricted(header))
                {
                    targetRequest.Headers.TryAddWithoutValidation(header, request.Headers[header]);
                }
            }
            #region Logging to db
            //// Log details of targetRequest before sending
            //var method = targetRequest.Method.ToString();
            //var uri = targetRequest.RequestUri?.ToString() ?? "Unknown URI";

            //// Serialize headers to a string
            //var headers = string.Join("\n", targetRequest.Headers.Select(h => $"{h.Key}: {string.Join(", ", h.Value)}"));

            //// Read content if it exists (for POST/PUT requests)
            //string content = "";
            //if (targetRequest.Content != null)
            //{
            //    content = await targetRequest.Content.ReadAsStringAsync();
            //}

            //// Log to the database
            //if(targetRequest.Method == HttpMethod.Post)
            //    _databaseLogger.LogPostRequest(uri, headers, content);
            #endregion


            using var response = await _httpClient.SendAsync(targetRequest, HttpCompletionOption.ResponseHeadersRead);
            var responseStream = await response.Content.ReadAsStreamAsync();            

            // Handle gzipped content
            Stream processedStream = response.Content.Headers.ContentEncoding.Contains("gzip")
                ? new GZipStream(responseStream, CompressionMode.Decompress)
                : responseStream;

            // Prepare the response to the browser
            using var memoryStream = new MemoryStream();
            await processedStream.CopyToAsync(memoryStream);
            byte[] responseBody = memoryStream.ToArray();

            // Set response headers
            context.Response.ContentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = string.Join(",", header.Value);
            }

            // Forward Set-Cookie headers back to the browser to maintain session
            if (response.Headers.Contains("Set-Cookie"))
            {
                foreach (var cookie in response.Headers.GetValues("Set-Cookie"))
                {
                    context.Response.Headers.Add("Set-Cookie", cookie);
                }
            }

            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentLength64 = responseBody.Length;
            await context.Response.OutputStream.WriteAsync(responseBody, 0, responseBody.Length);

            LogMessage?.Invoke($"Request completed: {request.Url}");
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error processing request: {ex.Message}");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            byte[] errorMessage = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
            await context.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }

    private async Task HandleCssBundleRequest(HttpListenerContext context, BundleConfig bundle)
    {
        // Combine the CSS files in the bundle
        var combinedCss = new StringBuilder();

        foreach (var file in bundle.Files)
        {
            // Build the URL to fetch the CSS file from the target server
            string fileUrl = $"{targetRootUrl.TrimEnd('/')}/{file.TrimStart('~', '/')}";

            try
            {
                // Send a GET request to fetch the CSS file from the target server
                var fileResponse = await _httpClient.GetAsync(fileUrl);

                if (fileResponse.IsSuccessStatusCode)
                {
                    // Read the CSS content and append to combinedCss
                    string fileContent = await fileResponse.Content.ReadAsStringAsync();
                    combinedCss.AppendLine(fileContent);
                }
                else
                {
                    LogMessage?.Invoke($"Failed to fetch CSS file: {fileUrl}, Status: {fileResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Error fetching CSS file: {file}, Error: {ex.Message}");
            }
        }

        // Send the combined CSS to the browser
        byte[] responseBody = Encoding.UTF8.GetBytes(combinedCss.ToString());
        context.Response.ContentType = "text/css";
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentLength64 = responseBody.Length;
        await context.Response.OutputStream.WriteAsync(responseBody, 0, responseBody.Length);
    }


    private async Task HandleJsBundleRequest(HttpListenerContext context, BundleConfig bundle)
    {
        // Combine the JS files in the bundle
        var combinedJs = new StringBuilder();

        foreach (var file in bundle.Files)
        {
            // Build the URL to fetch the JS file from the target server
            string fileUrl = $"{targetRootUrl.TrimEnd('/')}/{file.TrimStart('~', '/')}";

            try
            {
                // Send a GET request to fetch the JS file from the target server
                var fileResponse = await _httpClient.GetAsync(fileUrl);

                if (fileResponse.IsSuccessStatusCode)
                {
                    // Read the JS content and append to combinedJs
                    string fileContent = await fileResponse.Content.ReadAsStringAsync();
                    combinedJs.AppendLine(fileContent);
                }
                else
                {
                    LogMessage?.Invoke($"Failed to fetch JS file: {fileUrl}, Status: {fileResponse.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                LogMessage?.Invoke($"Error fetching JS file: {file}, Error: {ex.Message}");
            }
        }

        // Send the combined JS to the browser
        byte[] responseBody = Encoding.UTF8.GetBytes(combinedJs.ToString());
        context.Response.ContentType = "application/javascript";
        context.Response.StatusCode = (int)HttpStatusCode.OK;
        context.Response.ContentLength64 = responseBody.Length;
        await context.Response.OutputStream.WriteAsync(responseBody, 0, responseBody.Length);
    }

    private async Task HandleVirtualUrlsRequest(HttpListenerContext context, string requestUrl)
    {
        try
        {
            // Process the regular request if it's not a bundled request
            string targetUrlString = targetRootUrl.TrimEnd('/') + requestUrl;
            if (!string.IsNullOrEmpty(context.Request.Url?.Query))
            {
                string query = context.Request.Url.Query;
                targetUrlString += Uri.EscapeUriString(query); // Ensure query parameters are URL-encoded
            }
            var fileResponse = await _httpClient.GetAsync(targetUrlString);

            // Check if the request to fetch the resource was successful
            if (fileResponse.IsSuccessStatusCode)
            {
                // Retrieve the content type of the resource
                var contentType = fileResponse.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
                context.Response.ContentType = contentType;

                // Handle different content types
                if (contentType.Contains("image"))
                {
                    // Handle image content (e.g., captcha)
                    //context.Response.StatusCode = (int)HttpStatusCode.OK;
                    //using var responseStream = await fileResponse.Content.ReadAsStreamAsync();
                    //await responseStream.CopyToAsync(context.Response.OutputStream);
                    var targetRequest = new HttpRequestMessage(HttpMethod.Get, targetUrlString);
                    using var response = await _httpClient.SendAsync(targetRequest);
                    var responseStream = await response.Content.ReadAsStreamAsync();

                    // Forward the image as it is without modification
                    context.Response.ContentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
                    context.Response.StatusCode = (int)response.StatusCode;
                    await responseStream.CopyToAsync(context.Response.OutputStream);
                }
                else if (contentType.Contains("javascript"))
                {
                    // Handle JavaScript content
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    using var responseStream = await fileResponse.Content.ReadAsStreamAsync();
                    await responseStream.CopyToAsync(context.Response.OutputStream);
                }
                else if (contentType.Contains("css"))
                {
                    // Handle CSS content
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    using var responseStream = await fileResponse.Content.ReadAsStreamAsync();
                    await responseStream.CopyToAsync(context.Response.OutputStream);
                }
                else
                {
                    // Handle any other types (e.g., text, fonts, etc.)
                    context.Response.StatusCode = (int)HttpStatusCode.OK;
                    using var responseStream = await fileResponse.Content.ReadAsStreamAsync();
                    await responseStream.CopyToAsync(context.Response.OutputStream);
                }

                LogMessage?.Invoke($"Successfully forwarded virtual URL request: {requestUrl}");
            }
            else
            {
                LogMessage?.Invoke($"Failed to fetch virtual URL resource: {requestUrl}, Status: {fileResponse.StatusCode}");
                context.Response.StatusCode = (int)fileResponse.StatusCode;
            }
        }
        catch (Exception ex)
        {
            LogMessage?.Invoke($"Error fetching virtual URL resource: {requestUrl}, Error: {ex.Message}");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            byte[] errorMessage = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
            await context.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
        }
    }
    private async Task HandleImageRequest(HttpListenerContext context, string _targetRootUrl)
    {
        try
        {
            var request = context.Request;
            Console.WriteLine($"Request: {request.HttpMethod} {request.Url}");

            // Reconstruct the target URL
            var targetUrl = $"{_targetRootUrl}{request.Url.AbsolutePath}{request.Url.Query}";

            using var httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true, // Enable cookie handling
                CookieContainer = new CookieContainer()
            };

            using var httpClient = new HttpClient(httpClientHandler);
            var targetRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUrl);

            // Copy request body if present
            if (request.HasEntityBody)
            {
                using var bodyReader = new StreamReader(request.InputStream, request.ContentEncoding);
                var bodyContent = await bodyReader.ReadToEndAsync();
                targetRequest.Content = new StringContent(bodyContent, Encoding.UTF8, request.ContentType);
            }

            // Copy all headers
            foreach (string header in request.Headers)
            {
                if (!WebHeaderCollection.IsRestricted(header))
                {
                    targetRequest.Headers.TryAddWithoutValidation(header, request.Headers[header]);
                }
            }

            // Ensure User-Agent and Referer are forwarded
            targetRequest.Headers.UserAgent.ParseAdd(request.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            if (!string.IsNullOrEmpty(request.UrlReferrer?.ToString()))
            {
                targetRequest.Headers.Referrer = request.UrlReferrer;
            }

            // Get the response from the target server
            using var response = await httpClient.SendAsync(targetRequest, HttpCompletionOption.ResponseHeadersRead);
            var responseStream = await response.Content.ReadAsStreamAsync();

            // Handle gzipped content
            Stream processedStream;
            if (response.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                processedStream = new GZipStream(responseStream, CompressionMode.Decompress);
            }
            else
            {
                processedStream = responseStream;
            }

            // Copy cookies back to the browser
            foreach (Cookie cookie in httpClientHandler.CookieContainer.GetCookies(new Uri(_targetRootUrl)))
            {
                context.Response.AppendCookie(new System.Net.Cookie(cookie.Name, cookie.Value));
            }

            // Read the decompressed or original content
            using var memoryStream = new MemoryStream();
            await processedStream.CopyToAsync(memoryStream);
            byte[] responseBody = memoryStream.ToArray();

            // Set content type and headers
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            context.Response.ContentType = contentType;
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = string.Join(",", header.Value);
            }

            // Send the response back to the browser
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentLength64 = responseBody.Length;
            await context.Response.OutputStream.WriteAsync(responseBody, 0, responseBody.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            byte[] errorMessage = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
            await context.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }
    private async Task HandleImageRequest1(HttpListenerContext context, string _targetRootUrl)
    {
        try
        {
            var request = context.Request;
            Console.WriteLine($"Request: {request.HttpMethod} {request.Url}");

            // Reconstruct the target URL
            var targetUrl = $"{_targetRootUrl}{request.Url.AbsolutePath}{request.Url.Query}";

            using var httpClientHandler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                UseCookies = true, // Enable cookie handling
                CookieContainer = new CookieContainer()
            };

            using var httpClient = new HttpClient(httpClientHandler);
            var targetRequest = new HttpRequestMessage(new HttpMethod(request.HttpMethod), targetUrl);

            // Copy request body if present
            if (request.HasEntityBody)
            {
                using var bodyReader = new StreamReader(request.InputStream, request.ContentEncoding);
                var bodyContent = await bodyReader.ReadToEndAsync();
                targetRequest.Content = new StringContent(bodyContent, Encoding.UTF8, request.ContentType);
            }

            // Copy all headers
            foreach (string header in request.Headers)
            {
                if (!WebHeaderCollection.IsRestricted(header))
                {
                    targetRequest.Headers.TryAddWithoutValidation(header, request.Headers[header]);
                }
            }

            // Ensure User-Agent and Referer are forwarded
            targetRequest.Headers.UserAgent.ParseAdd(request.UserAgent ?? "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            if (!string.IsNullOrEmpty(request.UrlReferrer?.ToString()))
            {
                targetRequest.Headers.Referrer = request.UrlReferrer;
            }

            // Get the response from the target server
            using var response = await httpClient.SendAsync(targetRequest, HttpCompletionOption.ResponseHeadersRead);
            var responseStream = await response.Content.ReadAsStreamAsync();

            // Handle gzipped content
            Stream processedStream;
            if (response.Content.Headers.ContentEncoding.Contains("gzip"))
            {
                processedStream = new GZipStream(responseStream, CompressionMode.Decompress);
            }
            else
            {
                processedStream = responseStream;
            }

            // Copy cookies back to the browser
            foreach (Cookie cookie in httpClientHandler.CookieContainer.GetCookies(new Uri(_targetRootUrl)))
            {
                context.Response.AppendCookie(new System.Net.Cookie(cookie.Name, cookie.Value));
            }

            // Read the decompressed or original content
            using var memoryStream = new MemoryStream();
            await processedStream.CopyToAsync(memoryStream);
            byte[] responseBody = memoryStream.ToArray();

            // Set content type and headers
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
            context.Response.ContentType = contentType;
            foreach (var header in response.Headers)
            {
                context.Response.Headers[header.Key] = string.Join(",", header.Value);
            }

            // Send the response back to the browser
            context.Response.StatusCode = (int)response.StatusCode;
            context.Response.ContentLength64 = responseBody.Length;
            await context.Response.OutputStream.WriteAsync(responseBody, 0, responseBody.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
            byte[] errorMessage = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
            await context.Response.OutputStream.WriteAsync(errorMessage, 0, errorMessage.Length);
        }
        finally
        {
            context.Response.OutputStream.Close();
        }
    }
}

public class BundleConfig
{
    public string Name { get; set; }
    public List<string> Files { get; set; }
}
