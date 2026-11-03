using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LifeOS.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LifeOS.Api.Tests;

public sealed class LifeOsEndToEndTests
{
    [Fact]
    public async Task AccountCanRegisterManagePrivateDataAndRestoreDocumentBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), $"lifeos-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        WebApplication? app = null;
        try
        {
            var database = Path.Combine(root, "lifeos.db");
            var documents = Path.Combine(root, "documents");
            app = await LifeOsApplication.BuildAsync(
            [
                "--urls=http://127.0.0.1:0",
                $"--ConnectionStrings:LifeOS=Data Source={database};Pooling=False",
                $"--Documents:StoragePath={documents}"
            ]);
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features
                .Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };

            foreach (var origin in new[] { "https://localhost", "https://lifeos.local" })
            {
                using var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/tasks");
                preflight.Headers.Add("Origin", origin);
                preflight.Headers.Add("Access-Control-Request-Method", "GET");
                using var preflightResponse = await client.SendAsync(preflight);
                Assert.Equal(origin, preflightResponse.Headers.GetValues("Access-Control-Allow-Origin").Single());
            }

            var accountA = await Register(client, "Avery", "avery@example.test", "StrongPass!12345");
            var taskId = await CreateTask(client, accountA.Token, "Original task");
            var documentId = await UploadDocument(client, accountA.Token, "backup document contents");

            using var backupResponse = await Send(client, HttpMethod.Get, "/api/export", accountA.Token);
            await AssertSuccess(backupResponse);
            var backupBytes = await backupResponse.Content.ReadAsByteArrayAsync();
            Assert.True(backupBytes.Length > 0);

            await CreateTask(client, accountA.Token, "Temporary task to be replaced");
            using (var deleteDocument = await Send(client, HttpMethod.Delete, $"/api/documents/{documentId}", accountA.Token))
                Assert.Equal(HttpStatusCode.NoContent, deleteDocument.StatusCode);

            using var restoreForm = new MultipartFormDataContent();
            restoreForm.Add(new ByteArrayContent(backupBytes), "backup", "lifeos-backup.zip");
            using var restoreRequest = new HttpRequestMessage(HttpMethod.Post, "/api/export/restore") { Content = restoreForm };
            restoreRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accountA.Token);
            using var restoreResponse = await client.SendAsync(restoreRequest);
            await AssertSuccess(restoreResponse);

            var restoredTaskId = 0;
            using (var tasksResponse = await Send(client, HttpMethod.Get, "/api/tasks", accountA.Token))
            using (var tasks = JsonDocument.Parse(await tasksResponse.Content.ReadAsStringAsync()))
            {
                Assert.Equal(HttpStatusCode.OK, tasksResponse.StatusCode);
                Assert.Equal(1, tasks.RootElement.GetArrayLength());
                restoredTaskId = tasks.RootElement[0].GetProperty("id").GetInt32();
                Assert.NotEqual(taskId, restoredTaskId);
                Assert.Equal("Original task", tasks.RootElement[0].GetProperty("title").GetString());
            }

            var restoredDocumentId = 0;
            using (var documentsResponse = await Send(client, HttpMethod.Get, "/api/documents", accountA.Token))
            using (var documentsJson = JsonDocument.Parse(await documentsResponse.Content.ReadAsStringAsync()))
            {
                Assert.Equal(HttpStatusCode.OK, documentsResponse.StatusCode);
                Assert.Equal(1, documentsJson.RootElement.GetArrayLength());
                restoredDocumentId = documentsJson.RootElement[0].GetProperty("id").GetInt32();
                Assert.NotEqual(documentId, restoredDocumentId);
                Assert.Equal("notes.txt", documentsJson.RootElement[0].GetProperty("fileName").GetString());
            }
            using (var download = await Send(client, HttpMethod.Get, $"/api/documents/{restoredDocumentId}/download", accountA.Token))
            {
                await AssertSuccess(download);
                Assert.Equal("backup document contents", await download.Content.ReadAsStringAsync());
            }

            using (var profileResponse = await Send(client, HttpMethod.Get, "/api/users/me", accountA.Token))
            using (var profile = JsonDocument.Parse(await profileResponse.Content.ReadAsStringAsync()))
            {
                await AssertSuccess(profileResponse);
                Assert.Equal("Avery", profile.RootElement.GetProperty("name").GetString());
            }

            var accountB = await Register(client, "Blake", "blake@example.test", "OtherStrong!54321");
            using (var otherTasksResponse = await Send(client, HttpMethod.Get, "/api/tasks", accountB.Token))
            using (var otherTasks = JsonDocument.Parse(await otherTasksResponse.Content.ReadAsStringAsync()))
            {
                Assert.Equal(HttpStatusCode.OK, otherTasksResponse.StatusCode);
                Assert.Empty(otherTasks.RootElement.EnumerateArray());
            }
            using (var crossAccountUpdate = await Send(client, HttpMethod.Put, $"/api/tasks/{restoredTaskId}", accountB.Token,
                JsonContent.Create(new { title = "tampered", status = "open", priority = "normal", recurrence = "none" })))
                Assert.Equal(HttpStatusCode.NotFound, crossAccountUpdate.StatusCode);

            using (var login = await client.PostAsJsonAsync("/api/auth/login", new { email = "avery@example.test", password = "StrongPass!12345" }))
                await AssertSuccess(login);
        }
        finally
        {
            if (app is not null)
            {
                await app.StopAsync();
                await app.DisposeAsync();
            }
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<(string Token, int UserId)> Register(HttpClient client, string name, string email, string password)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/register", new { name, email, password });
        await AssertSuccess(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (body.RootElement.GetProperty("token").GetString()!, body.RootElement.GetProperty("user").GetProperty("id").GetInt32());
    }

    private static async Task<int> CreateTask(HttpClient client, string token, string title)
    {
        using var response = await Send(client, HttpMethod.Post, "/api/tasks", token,
            JsonContent.Create(new { title, status = "open", priority = "normal", recurrence = "none" }));
        await AssertSuccess(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetInt32();
    }

    private static async Task<int> UploadDocument(HttpClient client, string token, string contents)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("Personal"), "category");
        form.Add(new StringContent("Notes"), "title");
        form.Add(new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(contents)), "file", "notes.txt");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await client.SendAsync(request);
        await AssertSuccess(response);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("id").GetInt32();
    }

    private static Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, path) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client.SendAsync(request);
    }

    private static async Task AssertSuccess(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode,
            $"Expected success but received {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }
}
