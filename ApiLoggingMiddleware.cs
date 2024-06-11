using System.Text;

namespace VMSystem
{
    public class ApiLoggingMiddleware
    {
        private readonly ILogger<ApiLoggingMiddleware> _logger;

        private readonly RequestDelegate _next;

        public ApiLoggingMiddleware(ILogger<ApiLoggingMiddleware> logger, RequestDelegate next)
        {
            _logger = logger;
            _next = next;
        }

        public async Task InvokeAsync(HttpContext context)
        {
            // 攔截請求
            var request = await FormatRequest(context.Request);

            // 紀錄回應前的狀態
            var originalBodyStream = context.Response.Body;

            using var responseBody = new MemoryStream();
            context.Response.Body = responseBody;

            await _next(context);

            // 攔截回應
            var response = await FormatResponse(context.Response);

            // 紀錄資訊
            _logger.LogInformation("Request: \n{Request}", request);
            _logger.LogInformation("Response: \n{Response}", response);


            // 將原始回應內容寫回
            await responseBody.CopyToAsync(originalBodyStream);
        }

        /// <summary>
        /// 格式化請求
        /// </summary>
        private async Task<string> FormatRequest(HttpRequest request)
        {
            request.EnableBuffering();
            var headers = FormatHeaders(request.Headers);
            var body = await new StreamReader(request.Body, Encoding.UTF8).ReadToEndAsync();
            request.Body.Position = 0;
            var ip = request.HttpContext.Connection.RemoteIpAddress?.ToString();

            return $"Method: {request.Method}\n" +
                   $"URL: {request.Scheme}://{request.Host}{request.Path}{request.QueryString}\n" +
                   $"Body: {body}\n" +
                   $"IP: {ip}";
        }

        /// <summary>
        /// 格式化回應
        /// </summary>
        private async Task<string> FormatResponse(HttpResponse response)
        {
            response.Body.Seek(0, SeekOrigin.Begin);
            var headers = FormatHeaders(response.Headers);
            var body = await new StreamReader(response.Body, Encoding.UTF8).ReadToEndAsync();
            response.Body.Seek(0, SeekOrigin.Begin);

            return $"Status code: {response.StatusCode}\n" +
                   $"Headers: \n{headers}" +
                   $"Body: {body}";
        }

        /// <summary>
        /// 格式化標頭
        /// </summary>
        private string FormatHeaders(IHeaderDictionary headers)
        {
            var formattedHeaders = new StringBuilder();
            foreach (var (key, value) in headers)
            {
                formattedHeaders.AppendLine($"\t{key}: {string.Join(",", value)}");
            }

            return formattedHeaders.ToString();
        }
    }
}
