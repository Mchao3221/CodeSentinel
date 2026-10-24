using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CodeSentinel.Tests;

/// <summary>
/// 集成测试共用的 HTTP 客户端助手：处理登录态与 CSRF 令牌。
/// </summary>
internal static class TestApiClient
{
    /// <summary>
    /// CSRF 请求头名称。
    /// </summary>
    public const string CsrfHeaderName = "X-CSRF-TOKEN";

    /// <summary>
    /// 创建保留 Cookie 的客户端并登录。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <param name="username">登录名</param>
    /// <param name="password">密码</param>
    /// <returns>已登录的客户端</returns>
    public static async Task<HttpClient> LoginAsync(
        this TestAppFactory factory,
        string username,
        string password)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true,
            AllowAutoRedirect = false
        });

        using var response = await client.PostAsJsonAsync(
            "/api/auth/login",
            new { username, password });

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"登录失败：{response.StatusCode} {body}");
        }

        return client;
    }

    /// <summary>
    /// 创建保留 Cookie 的客户端并以种子管理员登录。
    /// </summary>
    /// <param name="factory">测试宿主工厂</param>
    /// <returns>已登录的管理员客户端</returns>
    public static Task<HttpClient> LoginAsAdminAsync(this TestAppFactory factory)
    {
        return factory.LoginAsync(TestAppFactory.AdminUsername, TestAppFactory.AdminPassword);
    }

    /// <summary>
    /// 获取 CSRF 令牌，同时让客户端保存 antiforgery Cookie。
    /// </summary>
    /// <param name="client">HTTP 客户端</param>
    /// <returns>请求头令牌</returns>
    public static async Task<string> GetCsrfTokenAsync(this HttpClient client)
    {
        var body = await client.GetFromJsonAsync<CsrfBody>("/api/auth/csrf");
        return body!.Token;
    }

    /// <summary>
    /// 携带 CSRF 令牌发送 JSON POST 请求。
    /// </summary>
    /// <param name="client">HTTP 客户端</param>
    /// <param name="url">请求地址</param>
    /// <param name="body">请求体</param>
    /// <returns>响应</returns>
    public static Task<HttpResponseMessage> PostJsonWithCsrfAsync(
        this HttpClient client,
        string url,
        object body)
    {
        return SendJsonWithCsrfAsync(client, HttpMethod.Post, url, body);
    }

    /// <summary>
    /// 携带 CSRF 令牌发送 JSON PATCH 请求。
    /// </summary>
    /// <param name="client">HTTP 客户端</param>
    /// <param name="url">请求地址</param>
    /// <param name="body">请求体</param>
    /// <returns>响应</returns>
    public static Task<HttpResponseMessage> PatchJsonWithCsrfAsync(
        this HttpClient client,
        string url,
        object body)
    {
        return SendJsonWithCsrfAsync(client, HttpMethod.Patch, url, body);
    }

    /// <summary>
    /// 携带 CSRF 令牌发送 JSON PUT 请求。
    /// </summary>
    /// <param name="client">HTTP 客户端</param>
    /// <param name="url">请求地址</param>
    /// <param name="body">请求体</param>
    /// <returns>响应</returns>
    public static Task<HttpResponseMessage> PutJsonWithCsrfAsync(
        this HttpClient client,
        string url,
        object body)
    {
        return SendJsonWithCsrfAsync(client, HttpMethod.Put, url, body);
    }

    /// <summary>
    /// 携带 CSRF 令牌发送 JSON 请求。
    /// </summary>
    /// <param name="client">HTTP 客户端</param>
    /// <param name="method">HTTP 方法</param>
    /// <param name="url">请求地址</param>
    /// <param name="body">请求体</param>
    /// <returns>响应</returns>
    private static async Task<HttpResponseMessage> SendJsonWithCsrfAsync(
        HttpClient client,
        HttpMethod method,
        string url,
        object body)
    {
        var token = await client.GetCsrfTokenAsync();
        using var request = new HttpRequestMessage(method, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add(CsrfHeaderName, token);
        return await client.SendAsync(request);
    }
}
