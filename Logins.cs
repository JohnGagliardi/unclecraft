using System.Security.Cryptography;

namespace McShared;

/// <summary>Who plays: a remembered login (device token) or a name + password.</summary>
public abstract record PlayerChoice(string Name);
public sealed record SavedChoice(RememberedLogin Saved) : PlayerChoice(Saved.Person);
public sealed record PasswordChoice(string Person, string Password, bool Remember) : PlayerChoice(Person)
{
    // Never show the password in logs or debug output.
    public override string ToString() => $"PasswordChoice {{ Person = {Person}, Password = ***, Remember = {Remember} }}";
}
/// <summary>First login with the host's invite code (normalized, 16 chars). The real name comes back from the host.</summary>
public sealed record InviteChoice(string Code, string? NewPassword = null) : PlayerChoice("Invite " + Code[..4])
{
    // Never show the chosen password in logs or debug output.
    public override string ToString() => $"InviteChoice {{ Code = {Code[..4]}…, NewPassword = {(NewPassword == null ? "none" : "***")} }}";
}

/// <summary>Split-screen player 2 as a guest of player 1 (no account of its own). The host names it "&lt;Person&gt;_P2".</summary>
public sealed record GuestChoice() : PlayerChoice("Guest");

/// <summary>Shared login logic for both windows: saved device, password, or invite code.</summary>
public static class Logins
{
    public const string InvitedPlaceholder = "Invited player";

    public static async Task<Lease> LoginAs(BrokerClient broker, PlayerChoice c, string hostKey)
    {
        switch (c)
        {
            case SavedChoice sc:
            {
                string token;
                try { token = RememberedStore.Token(sc.Saved); }
                catch (Exception e) when (e is CryptographicException or FormatException or KeyNotFoundException) // corrupt or gone for good
                {
                    RememberedStore.Remove(hostKey, sc.Saved.Person);
                    throw new BrokerException($"{sc.Saved.Person}'s saved login can't be read on this computer. Log in again.", "device_token_invalid");
                }
                catch (Exception e) // e.g. Keychain access denied or unlock cancelled: keep it, it may work next time
                {
                    Log.Error($"could not open the saved login for {sc.Saved.Person}", e);
                    throw new BrokerException($"Couldn't open {sc.Saved.Person}'s saved login. Try again.", "saved_login_locked");
                }
                try
                {
                    var lease = await broker.LoginWithDevice(token);
                    RememberedStore.Rename(hostKey, sc.Saved.Person, lease.Person);
                    return lease;
                }
                catch (BrokerException e) when (e.Code == "device_token_invalid")
                {
                    RememberedStore.Remove(hostKey, sc.Saved.Person);
                    throw new BrokerException($"{sc.Saved.Person}'s saved login expired or was removed by the host. Log in again (password or a new invite code).", e.Code);
                }
            }
            case InviteChoice ic:
                try
                {
                    var lease = await broker.LoginWithInvite(ic.Code, ic.NewPassword);
                    if (lease.DeviceToken != null) RememberedStore.Add(hostKey, lease.Person, lease.DeviceId ?? "", lease.DeviceToken);
                    return lease;
                }
                catch (BrokerException e) when (e.Body?["deviceToken"] is { } dt) // code spent, PC registered, but no free account
                {
                    RememberedStore.Add(hostKey, e.Body?["person"]?.GetValue<string>() ?? InvitedPlaceholder, e.Body?["deviceId"]?.GetValue<string>() ?? "", dt.GetValue<string>());
                    throw;
                }
            default:
            {
                var pc = (PasswordChoice)c;
                try
                {
                    var lease = await broker.Login(pc.Person, pc.Password, pc.Remember);
                    if (lease.DeviceToken != null) RememberedStore.Add(hostKey, lease.Person, lease.DeviceId ?? "", lease.DeviceToken);
                    return lease;
                }
                catch (BrokerException e) when (e.Body?["deviceToken"] is { } dt) // password right, but no free account
                {
                    RememberedStore.Add(hostKey, pc.Person, e.Body?["deviceId"]?.GetValue<string>() ?? "", dt.GetValue<string>());
                    throw;
                }
            }
        }
    }
}
