using Microsoft.AspNetCore.Mvc;
using Azure;
using Azure.AI.DocumentIntelligence;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Authorization;
using System;
using System.Collections.Generic;
using System.Linq;

[Route("api/resume")]
[ApiController]
public class ResumeController : ControllerBase
{
    private readonly string _endpoint;
    private readonly string _apiKey;
    private readonly long _maxFileSize = 10 * 1024 * 1024; // 10MB
    private readonly List<string> _allowedFileTypes = new() { ".pdf", ".docx", ".doc", ".txt", ".rtf" };
    private readonly IConfiguration _configuration;

    public ResumeController(IConfiguration configuration)
    {
        _configuration = configuration;
        _endpoint = _configuration["Azure:DocumentIntelligence:Endpoint"];
        _apiKey = _configuration["Azure:DocumentIntelligence:ApiKey"];

        if (string.IsNullOrEmpty(_endpoint) || string.IsNullOrEmpty(_apiKey))
        {
            throw new ArgumentNullException("Azure Document Intelligence configuration is missing");
        }
    }

    // Validate file before processing
    private IActionResult ValidateFile(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("Invalid file. Please upload a valid resume.");

        if (file.Length > _maxFileSize)
            return BadRequest($"File size exceeds {_maxFileSize / (1024 * 1024)}MB.");

        var fileExtension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (!_allowedFileTypes.Contains(fileExtension))
            return BadRequest($"File type not allowed. Allowed types: {string.Join(", ", _allowedFileTypes)}");

        return null; // No validation issues
    }

    [HttpPost("upload")]
    public async Task<IActionResult> UploadResume(IFormFile file)
    {
        try
        {
            var validationResponse = ValidateFile(file);
            if (validationResponse != null) return validationResponse;

            string uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            Directory.CreateDirectory(uploadsFolder); // Ensures the directory exists

            string uniqueFileName = $"{Guid.NewGuid()}{Path.GetExtension(file.FileName)}";
            string filePath = Path.Combine(uploadsFolder, uniqueFileName);

            await using var stream = new FileStream(filePath, FileMode.Create);
            await file.CopyToAsync(stream);

            return Ok(new { message = "Resume uploaded successfully", fileName = uniqueFileName });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred: {ex.Message}");
        }
    }

    [HttpPost("analyze")]
    public async Task<IActionResult> AnalyzeResume([FromForm] IFormFile file)
    {
        try
        {
            var validationResponse = ValidateFile(file);
            if (validationResponse != null) return validationResponse;

            await using var memoryStream = new MemoryStream();
            await file.CopyToAsync(memoryStream);
            memoryStream.Position = 0; // Reset stream position

            var credential = new AzureKeyCredential(_apiKey);
            var client = new DocumentIntelligenceClient(new Uri(_endpoint), credential);

            try
            {
                var operation = await client.AnalyzeDocumentAsync(
                    WaitUntil.Completed,
                    "prebuilt-layout",
                    BinaryData.FromStream(memoryStream));

                var result = operation.Value;

              var keyValuePairs = (result.KeyValuePairs != null)
    ? result.KeyValuePairs.Select(kvp => (object)new { Key = kvp.Key?.Content, Value = kvp.Value?.Content }).ToList()
    : new List<object>();

                return Ok(new
                {
                    text = result.Content,
                    fields = keyValuePairs
                });
            }
            catch (RequestFailedException ex)
            {
                return BadRequest(new { message = $"Azure Document Analysis failed: {ex.Message}" });
            }
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred: {ex.Message}");
        }
    }

    [HttpPost("cleanup")]
    public IActionResult CleanupOldFiles()
    {
        try
        {
            string uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "Uploads");
            if (!Directory.Exists(uploadsFolder))
                return Ok("No files to clean up.");

            int deletedCount = 0;
            DateTime cutoffTime = DateTime.Now.AddHours(-24);

            foreach (var file in Directory.GetFiles(uploadsFolder))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.LastWriteTime < cutoffTime)
                {
                    fileInfo.Delete();
                    deletedCount++;
                }
            }

            return Ok(new { message = $"Cleanup completed. {deletedCount} files removed." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, $"An error occurred during cleanup: {ex.Message}");
        }
    }
}