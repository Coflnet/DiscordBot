using System;
using System.Threading.Tasks;
using MessagePack;
using Newtonsoft.Json;
using StackExchange.Redis;
using Coflnet.Sky.Chat.Client.Client;
using Microsoft.Extensions.Configuration;
using Coflnet.Sky.Chat.Client.Model;
using System.Diagnostics;
using System.Collections.Generic;
using System.Linq;
using Discord;
using Coflnet.Sky.Chat.Client.Api;

namespace Coflnet.Discord;

public class ChatService
{
    private ChatApi api;
    private string chatAuthKey;
    private List<string> mutedUuids;
    public List<string> MutedUuids => mutedUuids;
    private IConnectionMultiplexer chatConnection;
    public ChatService(IConfiguration config,
        IConnectionMultiplexer chatConnection)
    {
        api = new(config["CHAT_BASE_URL"]);
        chatAuthKey = config["CHAT_API_KEY"];
        RefreshMutedUsers();
        this.chatConnection = chatConnection;
    }
    public async Task<ChannelMessageQueue> Subscribe(Func<ChatMessage, bool> onMessage)
    {
        return await SubscribeToChannel("chat", onMessage);
    }

    public async Task<ChannelMessageQueue> SubscribeToChannel(string channel, Func<ChatMessage, bool> onMessage)
    {
        for (int i = 0; i < 3; i++)
        {
            try
            {
                var sub = await GetCon().SubscribeAsync(channel);

                sub.OnMessage((value) =>
                {
                    var message = JsonConvert.DeserializeObject<ChatMessage>(value.Message);
                    if (!onMessage(message))
                        sub.Unsubscribe();
                });
                return sub;
            }
            catch (Exception)
            {
                if (i >= 2)
                    throw;
                await Task.Delay(300).ConfigureAwait(false);
            }
        }
        throw new Core.ApiException("connection_failed", "connection to chat failed");
    }

    public async Task SendToChannel(string channel, ChatMessage message)
    {
        await GetCon().PublishAsync(channel, JsonConvert.SerializeObject(message));
    }

    private ISubscriber GetCon()
    {
        return chatConnection.GetSubscriber();
    }

    public async Task<List<string>> GetMuteUuids()
    {
        return (await api.ApiChatMutesGetAsync(chatAuthKey)).Select(m => m.Uuid).ToList();
    }

    private void RefreshMutedUsers()
    {
        Task.Run(async () =>
        {
            try
            {
                mutedUuids = await GetMuteUuids();
                Console.WriteLine($" {mutedUuids.Count} muted users refreshed");
            }
            catch (Exception e)
            {
                Activity.Current?.Log(e.Message);
            }
        });
    }

    public async Task Send(ModChatMessage message)
    {
        try
        {
            var chatMsg = new ChatMessage(
                message.SenderUuid,
                message.SenderName,
                "",
                message.Message);
            await api.ApiChatSendPostAsync(chatAuthKey, chatMsg);
        }
        catch (ApiException e)
        {
            RefreshMutedUsers();
            Console.WriteLine(e.Message);
            throw JsonConvert.DeserializeObject<Core.ApiException>(e.Message?.Replace("Error calling ApiChatSendPost: ", ""));
        }
    }

    public async Task Mute(Mute mute)
    {
        await api.ApiChatMutePostAsync(chatAuthKey, mute);
        RefreshMutedUsers();
    }

    [MessagePackObject]
    public class ModChatMessage
    {
        [Key(0)]
        public string SenderName;
        [Key(1)]
        public string Message;
        [Key(3)]
        public string SenderUuid;
    }
}
