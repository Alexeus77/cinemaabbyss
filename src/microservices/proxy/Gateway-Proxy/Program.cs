using System.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

var configuration = builder.Configuration;

var monolithUrl = configuration["MONOLITH_URL"]!;
var moviesServiceUrl = configuration["MOVIES_SERVICE_URL"];
var eventsServiceUrl = configuration["EVENTS_SERVICE_URL"];
var gradualMigration = configuration.GetValue<bool>("GRADUAL_MIGRATION");
var moviesMigrationPercent = configuration.GetValue<int>("MOVIES_MIGRATION_PERCENT", 0);

builder.Services.AddHttpClient();

var app = builder.Build();

app.Use(async (context, next) =>
{
    if (context.Request.Path.Value != null)
    {
        var path = context.Request.Path.Value.ToLowerInvariant();
        var httpClient = context.RequestServices.GetRequiredService<IHttpClientFactory>().CreateClient();
        var targetUrl = monolithUrl;

        if (gradualMigration)
        {
            if (path.StartsWith("/api/movies") && !string.IsNullOrEmpty(moviesServiceUrl))
            {
                var random = new Random();
                if (random.Next(100) < moviesMigrationPercent)
                {
                    targetUrl = moviesServiceUrl;
                }
            }
            else if (path.StartsWith("/api/events") && !string.IsNullOrEmpty(eventsServiceUrl))
            {
                targetUrl = eventsServiceUrl;
            }
        }

        var targetUri = new Uri(new Uri(targetUrl), context.Request.Path + context.Request.QueryString);

        try
        {
            var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), targetUri);

            foreach (var header in context.Request.Headers)
            {
                if (!header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                {
                    requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
                }
            }

            // Копируем тело запроса (если есть)
            if (context.Request.ContentLength > 0)
            {
                var content = new StreamContent(context.Request.Body);
                if (context.Request.ContentType != null)
                    content.Headers.ContentType = new MediaTypeHeaderValue(context.Request.ContentType);
                requestMessage.Content = content;
            }

            var response = await httpClient.SendAsync(requestMessage);

            context.Response.StatusCode = (int)response.StatusCode;

            // Копируем заголовки ответа
            foreach (var header in response.Headers)
            {
                context.Response.Headers.TryAdd(header.Key, header.Value.ToArray());
            }

            context.Response.ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/octet-stream";
            context.Response.Headers.Remove("transfer-encoding");
            await response.Content.CopyToAsync(context.Response.Body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка проксирования {targetUri}: {ex.Message}");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        }
    }
    
    await next(context);
});

await app.RunAsync();