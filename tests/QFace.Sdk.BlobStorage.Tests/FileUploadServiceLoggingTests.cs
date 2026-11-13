using System.Net;
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using QFace.Sdk.BlobStorage.Models;
using QFace.Sdk.BlobStorage.Services;
using Xunit;

namespace QFace.Sdk.BlobStorage.Tests;

public class FileUploadServiceLoggingTests
{
    private const string Bucket = "qimerp-bucket";
    private const string Folder = "tenant-a/engagement/health/0196f000-0000-7000-8000-000000000001";
    private const string FileName = "ama-mensah-diagnosis";
    private const string Signature = "X-Amz-Signature=secret-signature";
    private static readonly string[] Forbidden =
        [FileName, "tenant-a", "engagement/health", Signature, "https://", "r2.cloudflarestorage.com", "cdn.example"];

    [Fact]
    public async Task No_Key_File_Name_Or_Url_Is_Ever_Logged()
    {
        var logger = new CapturingLogger();
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());
        s3.Setup(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DeleteObjectResponse());
        s3.Setup(c => c.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>()))
            .Returns<GetPreSignedUrlRequest>(r =>
                $"https://{Bucket}.{Bucket}.abc.r2.cloudflarestorage.com/{r.Key}?{Signature}");
        s3.Setup(c => c.GetObjectAsync(It.IsAny<GetObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception($"missing {Folder}/{FileName}.pdf") { StatusCode = HttpStatusCode.NotFound });
        var service = Service(s3.Object, logger);
        var bytes = Encoding.UTF8.GetBytes("%PDF");
        var file = Pdf(bytes);
        var key = $"{Folder}/{FileName}.pdf";

        var uploaded = await service.UploadPrivateFileAsync(file, Folder, FileName);
        await service.GetPreSignedUrlAsync(uploaded.SaveUrl, 15);
        await service.GetPreSignedUrlAsync(key, 15);
        await service.DeleteFileAsync(uploaded.SaveUrl);
        await service.GetObjectBytesAsync(key);
        await service.UploadContentAsync("text", key);
        await service.UploadStreamAsync(new MemoryStream(bytes), key, "application/pdf");
        await service.CreatePresignedPutUrlAsync(key, "application/pdf", bytes.Length);
        await service.UploadBase64ImageAsync(Convert.ToBase64String(bytes), Folder, FileName, "image/png");

        logger.Entries.Should().NotBeEmpty();
        foreach (var entry in logger.Entries)
            foreach (var forbidden in Forbidden)
                entry.Should().NotContain(forbidden);
    }

    [Fact]
    public async Task A_Failure_Is_Logged_Without_The_Key_Or_The_Url()
    {
        var logger = new CapturingLogger();
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new AmazonS3Exception("Access Denied") { StatusCode = HttpStatusCode.Forbidden, ErrorCode = "AccessDenied" });
        var service = Service(s3.Object, logger);

        var bytes = Encoding.UTF8.GetBytes("%PDF");
        var file = Pdf(bytes);

        var content = () => service.UploadContentAsync("text", $"{Folder}/{FileName}.pdf");
        var upload = () => service.UploadPrivateFileAsync(file, Folder, FileName);
        var invalid = () => service.GetPreSignedUrlAsync($"https://[cdn.example/{Folder}/{FileName}.pdf", 15);

        await content.Should().ThrowAsync<AmazonS3Exception>();
        await upload.Should().ThrowAsync<Exception>();
        (await invalid.Should().ThrowAsync<ArgumentException>()).Which.Message.Should().NotContain(FileName);
        logger.Entries.Should().NotBeEmpty();
        foreach (var entry in logger.Entries)
            foreach (var forbidden in Forbidden)
                entry.Should().NotContain(forbidden);
    }

    [Fact]
    public async Task A_Key_Is_Logged_Only_As_A_Short_Hash()
    {
        var logger = new CapturingLogger();
        var s3 = new Mock<IAmazonS3>();
        s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PutObjectResponse());
        var key = $"{Folder}/{FileName}.pdf";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();

        await Service(s3.Object, logger).UploadContentAsync("text", key);

        logger.Entries.Should().ContainSingle(entry => entry.Contains(hash));
        logger.Entries.Should().NotContain(entry => entry.Contains(FileName));
    }

    [Fact]
    public async Task A_Failure_Is_Logged_With_The_Exception_Type_And_Key_Ref_Only_Never_Its_Message()
    {
        var key = $"{Folder}/{FileName}.pdf";
        var bytes = Encoding.UTF8.GetBytes("%PDF");
        foreach (var failure in new Exception[]
                 {
                     new AmazonS3Exception($"denied {key} at https://cdn.example/{key}?{Signature}"),
                     new InvalidOperationException($"broken {key} at https://cdn.example/{key}?{Signature}")
                 })
        {
            var logger = new CapturingLogger();
            var s3 = new Mock<IAmazonS3>();
            s3.Setup(c => c.PutObjectAsync(It.IsAny<PutObjectRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            s3.Setup(c => c.DeleteObjectAsync(It.IsAny<DeleteObjectRequest>(), It.IsAny<CancellationToken>())).ThrowsAsync(failure);
            s3.Setup(c => c.GetPreSignedURL(It.IsAny<GetPreSignedUrlRequest>())).Throws(failure);
            var service = Service(s3.Object, logger);
            var file = Pdf(bytes);

            await ((Func<Task>)(() => service.DeleteFileAsync(key))).Should().ThrowAsync<Exception>();
            await ((Func<Task>)(() => service.GetPreSignedUrlAsync(key, 15))).Should().ThrowAsync<Exception>();
            await ((Func<Task>)(() => service.CreatePresignedPutUrlAsync(key, "application/pdf", bytes.Length))).Should().ThrowAsync<Exception>();
            if (failure is not AmazonS3Exception)
            {
                await ((Func<Task>)(() => service.UploadPrivateFileAsync(file, Folder, FileName))).Should().ThrowAsync<Exception>();
                await ((Func<Task>)(() => service.UploadBase64ImageAsync(Convert.ToBase64String(bytes), Folder, FileName, "image/png"))).Should().ThrowAsync<Exception>();
            }

            var errors = logger.Errors;
            errors.Should().HaveCount(failure is AmazonS3Exception ? 3 : 5);
            foreach (var entry in errors)
            {
                entry.Should().Contain(failure.GetType().Name);
                entry.Should().MatchRegex("key ref [0-9a-f]{12}");
                entry.Should().NotContain("denied").And.NotContain("broken");
                foreach (var forbidden in Forbidden)
                    entry.Should().NotContain(forbidden);
            }
            errors.Should().Contain(entry => entry.Contains($"key ref {Hash(key)}"));
        }
    }

    private static string Hash(string key) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12].ToLowerInvariant();

    private static IFormFile Pdf(byte[] bytes)
    {
        var file = new Mock<IFormFile>();
        file.SetupGet(f => f.FileName).Returns($"{FileName}.pdf");
        file.SetupGet(f => f.Length).Returns(bytes.Length);
        file.SetupGet(f => f.ContentType).Returns("application/pdf");
        file.Setup(f => f.CopyToAsync(It.IsAny<Stream>(), It.IsAny<CancellationToken>()))
            .Returns<Stream, CancellationToken>((target, ct) => target.WriteAsync(bytes, ct).AsTask());
        return file.Object;
    }

    private static FileUploadService Service(IAmazonS3 s3, ILogger<FileUploadService> logger) =>
        new(s3,
            Options.Create(new BlobStorageOptions
            {
                Provider = "CloudflareR2",
                Region = "auto",
                ServiceURL = "https://abc.r2.cloudflarestorage.com",
                Bucket = new BlobStorageBucketOptions { Name = Bucket, CdnBaseDomain = "cdn.example" }
            }),
            logger,
            Mock.Of<IImageResizeService>());

    private sealed class CapturingLogger : ILogger<FileUploadService>
    {
        public List<string> Entries { get; } = [];
        public List<string> Errors { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join("|", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            var entry = $"{formatter(state, exception)}|{values}|{exception?.Message}";
            Entries.Add(entry);
            if (logLevel == LogLevel.Error)
                Errors.Add(entry);
        }
    }
}
