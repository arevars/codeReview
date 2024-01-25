using Animalia.Backend.API.Interfaces;
using Animalia.Backend.API.Models.Configs;
using Animalia.Backend.API.Models.RequestModels.WebsiteAPI;
using Animalia.Backend.API.Models.ResponseModels.WebsiteAPI;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Animalia.Backend.API.Services.gRPC
{
    public class WebsiteAPIService : IWebsiteAPIService // @TODO refactor to have separate httpclient class
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly WebsiteAPIConfig _config;
        private readonly ILogger<WebsiteAPIService> _logger;

        private static readonly JsonSerializerOptions _defaultSerializerSettings = new JsonSerializerOptions() { PropertyNameCaseInsensitive = true, IgnoreNullValues = true };

        public WebsiteAPIService(
            IHttpClientFactory httpClientFactory,
            ILogger<WebsiteAPIService> logger,
            IOptions<WebsiteAPIConfig> config)
        {
            _httpClientFactory = httpClientFactory;
            _config = config.Value;
            _logger = logger;
        }

        public async Task<WebsiteAPIAuthResponseModel> AuthenticateByUsernamePassword(WebsiteAPIUsernamePasswordAuthRequestModel request)
        {
            var userWebsiteAPIResponseModel = new WebsiteAPIAuthResponseModel();
            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                JsonContent content = JsonContent.Create(request);

                var url = string.Concat(_config.Host, _config.AccountEndpoint);
                var httpResponseMessage = await httpClient.PostAsync(url, content);
                
                if (httpResponseMessage.IsSuccessStatusCode)
                {
                    using (var contentStream = await httpResponseMessage.Content.ReadAsStreamAsync())
                    {
                        userWebsiteAPIResponseModel = JsonSerializer.Deserialize<WebsiteAPIAuthResponseModel>(contentStream, _defaultSerializerSettings);
                    }
                }
            }
            catch(Exception e)
            {
                _logger.LogError(e.Message);
            }

            return userWebsiteAPIResponseModel;
        }

        public async Task<WebsiteAPIAuthResponseModel> AuthenticateByRememberMeToken(WebsiteAPITokenAuthRequestModel request)
        {
            var authResponseModel = new WebsiteAPIAuthResponseModel();
            try
            {
                var httpClient = _httpClientFactory.CreateClient();
                httpClient.DefaultRequestHeaders.Add("Authorization", "Bearer " + request.RememberMeToken);

                var url = string.Concat(_config.Host, _config.AccountEndpoint);
                var httpResponseMessage = await httpClient.PostAsync(url, null);

                if (httpResponseMessage.IsSuccessStatusCode)
                {
                    using (var contentStream = await httpResponseMessage.Content.ReadAsStreamAsync())
                    {
                        authResponseModel = JsonSerializer.Deserialize<WebsiteAPIAuthResponseModel>(contentStream, _defaultSerializerSettings);
                    }
                }

            }
            catch(Exception e)
            {
                _logger.LogError(e.Message);
            }

            return authResponseModel;
        }
    }
}
