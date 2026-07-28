

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using System.Text.Json.Serialization;
using Newtonsoft.Json;
using RestSharp;

public class LokiQuery
{
    private readonly ILogger<LokiQuery> logger;
    private readonly IConfiguration configuration;

    public LokiQuery(ILogger<LokiQuery> logger, IConfiguration configuration)
    {
        this.logger = logger;
        this.configuration = configuration;
    }

    internal async Task<IEnumerable<string>> GetVpsLog(Guid instance, DateTimeOffset from, DateTimeOffset to, int limit = 20, bool addTime = false)
    {
        var query = $"{{instance_id=\"{instance}\"}}";
        var start = from.ToUnixTimeSeconds();
        var end = to.ToUnixTimeSeconds();
        return await QueryLoki(query, start, end, limit, addTime);
    }

    private async Task<IEnumerable<string>> QueryLoki(string query, long start, long end, int limit = 20, bool addTime = false)
    {
        var client = new RestClient(configuration["LOKI_BASE_URL"]);
        var request = new RestRequest("loki/api/v1/query_range", RestSharp.Method.Get);
        request.AddQueryParameter("query", query);
        request.AddQueryParameter("start", start);
        request.AddQueryParameter("end", end);
        request.AddQueryParameter("limit", limit);
        var response = await client.ExecuteAsync(request);
        logger.LogInformation($"Querying loki with {client.BuildUri(request)}");
        if (!response.IsSuccessful)
        {
            logger.LogError($"Failed to query loki: {response.Content}");
            return Enumerable.Empty<string>();
        }
        var root = JsonConvert.DeserializeObject<Root>(response.Content);
        return root.data.result.SelectMany(r => r.values).Select(v => (addTime ? DateTimeOffset.FromUnixTimeMilliseconds(long.Parse(v[0]) / 1000000).ToString("yyyy-MM-dd HH:mm:ss: ") : "") + v[1]).Reverse();
    }

    public class Root
    {
        [JsonPropertyName("status")]
        public string status { get; set; }

        [JsonPropertyName("data")]
        public Data data { get; set; }
    }

    public class Data
    {
        [JsonPropertyName("result")]
        public Result[] result { get; set; }
    }

    public class Result
    {
        [JsonPropertyName("stream")]
        public StreamResponse stream { get; set; }

        [JsonPropertyName("values")]
        public string[][] values { get; set; }
    }

    public class StreamResponse
    {
        [JsonPropertyName("container")]
        public string container { get; set; }

        [JsonPropertyName("instance_id")]
        public string instance_id { get; set; }
        [JsonPropertyName("user_id")]
        public string user_id { get; set; }
    }
}
