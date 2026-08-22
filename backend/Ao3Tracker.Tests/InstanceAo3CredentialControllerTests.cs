using System.Text.Json;
using Ao3Tracker.Api.Dtos;
using Ao3Tracker.Api.Models;
using Ao3Tracker.Api.Services.Credentials;
using Microsoft.AspNetCore.Mvc;

namespace Ao3Tracker.Tests;

/// <summary>
/// The admin endpoints over the deployment's one AO3 login.
///
/// Run against the real store, real Data Protection and a real UserManager rather than mocks,
/// because everything worth pinning here is a property of that composition: that only an admin can
/// reach it, that a saved password survives the request that saved it, that no response ever
/// carries a secret back out, and that replacing the password discards the session cached under the
/// old one.
/// </summary>
public class InstanceAo3CredentialControllerTests : IDisposable
{
    private readonly LibraryTestHost _host = new();

    public void Dispose()
    {
        _host.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- who may touch it ----------------------------------------------------------------------

    [Fact]
    public async Task A_non_admin_may_not_read_the_credential_status()
    {
        var sam = _host.SeedUser("sam");

        var result = await _host.AdminAo3Credential(sam).GetStatus(default);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task A_non_admin_may_not_save_a_credential()
    {
        var sam = _host.SeedUser("sam");

        var result = await _host.AdminAo3Credential(sam)
            .SetCredential(new SetInstanceAo3CredentialRequest("scraper", "hunter2"), default);

        Assert.IsType<ForbidResult>(result.Result);

        // The refusal has to be a refusal to write, not merely a refusal to answer.
        Assert.False(await _host.WithCredentialStoreAsync(s => s.HasCredentialAsync()));
    }

    [Fact]
    public async Task A_non_admin_may_not_clear_the_credential()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);
        var sam = _host.SeedUser("sam");
        await Save(emma, "scraper", "hunter2");

        var result = await _host.AdminAo3Credential(sam).RemoveCredential(default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.True(await _host.WithCredentialStoreAsync(s => s.HasCredentialAsync()));
    }

    // ---- the round trip ------------------------------------------------------------------------

    [Fact]
    public async Task A_fresh_instance_reports_no_credential()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);

        var status = Body(await _host.AdminAo3Credential(emma).GetStatus(default));

        Assert.False(status.HasCredential);
        Assert.Null(status.Ao3Username);
        Assert.False(status.HasCachedSession);
        Assert.Null(status.SessionEstablishedAt);
        Assert.Null(status.SessionExpiresAt);
    }

    [Fact]
    public async Task Saving_a_credential_persists_it_and_reports_the_username_back()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);

        var saved = Body(await Save(emma, "  shipwatcharr  ", "hunter2"));

        Assert.True(saved.HasCredential);
        Assert.Equal("shipwatcharr", saved.Ao3Username);
        Assert.False(saved.HasCachedSession);

        // Read back through a store of its own: what the scraper will find, not what the request's
        // change tracker still remembers.
        var stored = await _host.WithCredentialStoreAsync(s => s.GetDecryptedCredentialAsync());
        Assert.Equal(("shipwatcharr", "hunter2"), stored);

        var reread = Body(await _host.AdminAo3Credential(emma).GetStatus(default));
        Assert.Equal("shipwatcharr", reread.Ao3Username);
    }

    [Fact]
    public async Task Clearing_removes_the_row_rather_than_blanking_it()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);
        await Save(emma, "shipwatcharr", "hunter2");

        var cleared = Body(await _host.AdminAo3Credential(emma).RemoveCredential(default));

        Assert.False(cleared.HasCredential);

        // HasCredentialAsync is what gates scraping, and it asks whether a row exists at all — a
        // blanked row would leave the gate open on a login that cannot work.
        Assert.False(await _host.WithCredentialStoreAsync(s => s.HasCredentialAsync()));
        Assert.Null(await _host.WithCredentialStoreAsync(s => s.GetUsernameAsync()));
    }

    [Fact]
    public async Task Clearing_when_nothing_is_stored_is_not_an_error()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);

        var cleared = Body(await _host.AdminAo3Credential(emma).RemoveCredential(default));

        Assert.False(cleared.HasCredential);
    }

    // ---- secrets stay in ------------------------------------------------------------------------

    [Fact]
    public async Task No_response_body_ever_carries_the_password_or_the_session_cookie()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);
        const string password = "correct-horse-battery-staple";
        const string cookie = "_otwarchive_session=deadbeef";

        var afterSave = Body(await Save(emma, "shipwatcharr", password));
        await _host.WithCredentialStoreAsync(async s =>
        {
            await s.SetSessionAsync(new Ao3Session(cookie, DateTime.UtcNow, DateTime.UtcNow.AddDays(7)));
            return true;
        });
        var afterSession = Body(await _host.AdminAo3Credential(emma).GetStatus(default));
        var afterClear = Body(await _host.AdminAo3Credential(emma).RemoveCredential(default));

        // Serialized rather than field-by-field: the assertion has to hold for whatever the DTO
        // grows later, not only for the fields it has today.
        foreach (var body in new[] { afterSave, afterSession, afterClear })
        {
            var json = JsonSerializer.Serialize(body);
            Assert.DoesNotContain(password, json, StringComparison.Ordinal);
            Assert.DoesNotContain(cookie, json, StringComparison.Ordinal);
        }

        // The status may say a session exists; it may not say what it is.
        Assert.True(afterSession.HasCachedSession);
        Assert.False(afterSave.HasCachedSession);
    }

    // ---- the session is a cache of the password --------------------------------------------------

    [Fact]
    public async Task Re_saving_the_credential_discards_the_cached_session()
    {
        var emma = _host.SeedUser("emma", isAdmin: true);
        await Save(emma, "shipwatcharr", "hunter2");
        await _host.WithCredentialStoreAsync(async s =>
        {
            await s.SetSessionAsync(new Ao3Session("_otwarchive_session=old", DateTime.UtcNow, null));
            return true;
        });

        Assert.True(Body(await _host.AdminAo3Credential(emma).GetStatus(default)).HasCachedSession);

        // A session established with the old password proves nothing about the new one.
        var afterResave = Body(await Save(emma, "shipwatcharr", "hunter3"));

        Assert.False(afterResave.HasCachedSession);
        Assert.Null(await _host.WithCredentialStoreAsync(s => s.GetSessionAsync()));
    }

    private Task<ActionResult<InstanceAo3CredentialDto>> Save(
        ApplicationUser admin, string username, string password) =>
        _host.AdminAo3Credential(admin)
            .SetCredential(new SetInstanceAo3CredentialRequest(username, password), default);

    private static InstanceAo3CredentialDto Body(ActionResult<InstanceAo3CredentialDto> result) =>
        Assert.IsType<InstanceAo3CredentialDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
}
