using Amazon.S3;
using Amazon.S3.Model;

namespace TripEx.Api.Services;

/// <summary>
/// Unified file storage — supports local filesystem or S3-compatible storage
/// </summary>
public class FileStorageService
{
    private readonly string _provider;
    private readonly string _localPath;
    private readonly IConfiguration _config;
    private readonly AmazonS3Client? _s3Client;

    public FileStorageService(IConfiguration config)
    {
        _config = config;
        _provider = config["Storage:Provider"]
            ?? Environment.GetEnvironmentVariable("STORAGE_PROVIDER")
            ?? "local";
        _localPath = config["Storage:LocalPath"]
            ?? Environment.GetEnvironmentVariable("STORAGE_PATH")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "storage");

        // Create S3 client once — reused for all operations in this scope
        if (_provider == "s3")
            _s3Client = CreateS3Client();
    }

    /// <summary>
    /// Read a file by its relative path (e.g. "knowledge/doc.pdf")
    /// </summary>
    public async Task<byte[]> ReadFileAsync(string relativePath)
    {
        if (_provider == "s3")
            return await ReadFromS3Async(relativePath);

        var fullPath = Path.Combine(_localPath, relativePath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"File not found: {relativePath}");

        return await File.ReadAllBytesAsync(fullPath);
    }

    /// <summary>
    /// Save a file and return its relative path
    /// </summary>
    public async Task<string> SaveFileAsync(string relativePath, byte[] data)
    {
        if (_provider == "s3")
            return await SaveToS3Async(relativePath, data);

        var fullPath = Path.Combine(_localPath, relativePath);
        var dir = Path.GetDirectoryName(fullPath);
        if (dir != null) Directory.CreateDirectory(dir);

        await File.WriteAllBytesAsync(fullPath, data);
        return relativePath;
    }

    /// <summary>
    /// Delete a file
    /// </summary>
    public async Task DeleteFileAsync(string relativePath)
    {
        if (_provider == "s3")
        {
            await DeleteFromS3Async(relativePath);
            return;
        }

        var fullPath = Path.Combine(_localPath, relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    // ── S3 helpers ──

    private AmazonS3Client CreateS3Client()
    {
        var endpoint = _config["Storage:S3:Endpoint"];
        var accessKey = _config["Storage:S3:AccessKey"]
            ?? Environment.GetEnvironmentVariable("S3_ACCESS_KEY") ?? "";
        var secretKey = _config["Storage:S3:SecretKey"]
            ?? Environment.GetEnvironmentVariable("S3_SECRET_KEY") ?? "";
        var region = _config["Storage:S3:Region"] ?? "us-east-1";

        var s3Config = new AmazonS3Config
        {
            RegionEndpoint = Amazon.RegionEndpoint.GetBySystemName(region)
        };

        // Support S3-compatible endpoints (MinIO, etc.)
        if (!string.IsNullOrEmpty(endpoint))
        {
            s3Config.ServiceURL = endpoint;
            s3Config.ForcePathStyle = true;
        }

        return new AmazonS3Client(accessKey, secretKey, s3Config);
    }

    private string GetBucketName() =>
        _config["Storage:S3:BucketName"]
        ?? Environment.GetEnvironmentVariable("S3_BUCKET_NAME")
        ?? "tripex-storage";

    private async Task<byte[]> ReadFromS3Async(string key)
    {
        var client = _s3Client!;
        var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = GetBucketName(),
            Key = key
        });

        using var ms = new MemoryStream();
        await response.ResponseStream.CopyToAsync(ms);
        return ms.ToArray();
    }

    private async Task<string> SaveToS3Async(string key, byte[] data)
    {
        var client = _s3Client!;
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = GetBucketName(),
            Key = key,
            InputStream = new MemoryStream(data)
        });
        return key;
    }

    private async Task DeleteFromS3Async(string key)
    {
        var client = _s3Client!;
        await client.DeleteObjectAsync(new DeleteObjectRequest
        {
            BucketName = GetBucketName(),
            Key = key
        });
    }
}
