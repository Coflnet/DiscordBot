

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
using Coflnet.Sky.Api.Client.Api;
using Discord;
using Discord.Interactions;
using Discord.WebSocket;
using Octokit.GraphQL;

public class McNameAutocompleteHandler(ISearchApi searchApi) : AutocompleteHandler
{
    public override async Task<AutocompletionResult> GenerateSuggestionsAsync(IInteractionContext context, IAutocompleteInteraction autocompleteInteraction, IParameterInfo parameter, IServiceProvider services)
    {
        string userInput = (context.Interaction as SocketAutocompleteInteraction).Data.Current.Value.ToString();
        if (string.IsNullOrEmpty(userInput))
        {
            return AutocompletionResult.FromSuccess(new AutocompleteResult[] { new AutocompleteResult("Technoblade", "b876ec32e396476ba1158438d83c67d4") });
        }
        var apiResult = await searchApi.ApiSearchPlayerPlayerNameGetAsync(userInput);
        IEnumerable<AutocompleteResult> results = apiResult.Select(a => new AutocompleteResult(a.Name, a.Uuid));

        return AutocompletionResult.FromSuccess(results.Take(25));
    }
}