using Coflnet.Sky.Api.Client.Model;
using Coflnet.Sky.McConnect.Api;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Discord;

public class UserInfoUpdaterTests
{
    [Test]
    public void UnverifiedMinecraftAccountThrowsBeforeSavingOrPayments()
    {
        // persistence, payments and profile client are null on purpose: touching any of them would throw a NullReferenceException
        var updater = new UserInfoUpdater(null!, null!, null!, System.Reflection.DispatchProxy.Create<IConnectApi, NullConnectApi>(), NullLogger<UserInfoUpdater>.Instance, new ConfigurationBuilder().Build());
        var existing = new DiscordAccountInfo();

        var ex = Assert.ThrowsAsync<MinecraftAccountNotVerifiedException>(() =>
            updater.UpdateuserDetails(1, new PlayerResult("Ekwav", Guid.NewGuid().ToString("n"), 0), existing));

        Assert.That(ex!.MinecraftName, Is.EqualTo("Ekwav"));
    }

    // IConnectApi is a large generated interface; only the lookup used here is stubbed via a proxy
    public class NullConnectApi : System.Reflection.DispatchProxy
    {
        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            var ret = targetMethod!.ReturnType;
            if (ret.IsGenericType && ret.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var inner = ret.GetGenericArguments()[0];
                return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(inner).Invoke(null, new object?[] { null });
            }
            return null;
        }
    }
}
