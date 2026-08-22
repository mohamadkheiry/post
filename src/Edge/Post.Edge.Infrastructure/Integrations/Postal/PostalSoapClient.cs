using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Extensions.Options;
using Post.Edge.Application.Ports;
using Post.Edge.Infrastructure.Options;
using Post.Edge.Infrastructure.Resilience;

namespace Post.Edge.Infrastructure.Integrations.Postal;

public static class PostalSoapParser
{
    public const int DefaultMaximumResponseCharacters = 1_048_576;
    public const int HardMaximumResponseCharacters = 16_777_216;

    public static ParcelInfoResult? Parse(
        string xml,
        int maximumResponseCharacters = DefaultMaximumResponseCharacters)
    {
        if (string.IsNullOrWhiteSpace(xml)) throw new ArgumentException("SOAP response is required.", nameof(xml));
        ValidateLimit(maximumResponseCharacters);
        if (xml.Length > maximumResponseCharacters)
            throw new InvalidOperationException("Postal SOAP response exceeded the configured size limit.");
        var document = ParseDocument(xml, maximumResponseCharacters);
        var fault = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "Fault");
        if (fault is not null)
        {
            throw new InvalidOperationException("Postal SOAP service returned a fault.");
        }

        var result = document.Descendants().FirstOrDefault(x => x.Name.LocalName == "GetDistributeInfoDataResult");
        if (result is null || string.IsNullOrWhiteSpace(result.Value) && !result.HasElements) return null;
        var scope = result.HasElements ? result : TryParseNestedXml(result.Value, maximumResponseCharacters);
        return new ParcelInfoResult(
            ParseDecimal(Value(scope, "Weight")),
            Value(scope, "BoxSize"),
            ParseDecimal(Value(scope, "PostalComm")),
            ParseBoolean(Value(scope, "IsJoof")));
    }

    private static XDocument ParseDocument(string value, int maximumResponseCharacters)
    {
        try
        {
            using var input = new StringReader(value);
            using var reader = XmlReader.Create(input, SecureReaderSettings(maximumResponseCharacters));
            return XDocument.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("Postal SOAP response did not contain valid XML.");
        }
    }

    private static XElement TryParseNestedXml(string value, int maximumResponseCharacters)
    {
        try
        {
            using var input = new StringReader(value);
            using var reader = XmlReader.Create(input, SecureReaderSettings(maximumResponseCharacters));
            return XElement.Load(reader, LoadOptions.None);
        }
        catch (XmlException)
        {
            throw new InvalidOperationException("Postal SOAP result did not contain valid XML.");
        }
    }

    private static XmlReaderSettings SecureReaderSettings(int maximumResponseCharacters) => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = maximumResponseCharacters,
        MaxCharactersFromEntities = 1,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    private static void ValidateLimit(int maximumResponseCharacters)
    {
        if (maximumResponseCharacters <= 0 || maximumResponseCharacters > HardMaximumResponseCharacters)
            throw new ArgumentOutOfRangeException(
                nameof(maximumResponseCharacters),
                "Postal SOAP response limit must be positive and at most 16 MiB characters.");
    }

    private static string? Value(XElement scope, string name) =>
        scope.DescendantsAndSelf().FirstOrDefault(x => x.Name.LocalName == name)?.Value.Trim();

    private static decimal? ParseDecimal(string? value) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static bool? ParseBoolean(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" => true,
        "false" or "0" => false,
        _ => null
    };
}

public sealed class PostalSoapClient(HttpClient httpClient, IOptions<PostalSoapOptions> options) : IParcelInfoClient
{
    public async Task<ParcelInfoResult?> GetAsync(
        string centerCode,
        string parcelBarcode,
        int userId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.Value.Endpoint))
            throw new InvalidOperationException("Postal SOAP endpoint is not configured.");
        XNamespace soap = "http://schemas.xmlsoap.org/soap/envelope/";
        XNamespace temp = "http://tempuri.org/";
        var envelope = new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(soap + "Envelope",
                new XAttribute(XNamespace.Xmlns + "soapenv", soap),
                new XAttribute(XNamespace.Xmlns + "tem", temp),
                new XElement(soap + "Header"),
                new XElement(soap + "Body",
                    new XElement(temp + "GetDistributeInfoData",
                        new XElement(temp + "PostnodeID", centerCode),
                        new XElement(temp + "ParcelCode", parcelBarcode),
                        new XElement(temp + "UserID", userId)))));
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Value.Endpoint)
        {
            Content = new StringContent(envelope.ToString(SaveOptions.DisableFormatting), Encoding.UTF8, "text/xml")
        };
        request.Headers.TryAddWithoutValidation("SOAPAction", options.Value.SoapAction);
        EdgeHttpResilience.MarkReplaySafe(request);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var maximumCharacters = options.Value.MaximumResponseCharacters;
        var responseXml = await ReadBoundedAsync(response.Content, maximumCharacters, cancellationToken);
        return PostalSoapParser.Parse(responseXml, maximumCharacters);
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        if (maximumCharacters <= 0 || maximumCharacters > PostalSoapParser.HardMaximumResponseCharacters)
            throw new InvalidOperationException("Postal SOAP response-size configuration is invalid.");
        if (content.Headers.ContentLength is { } contentLength && contentLength > maximumCharacters * 4L)
            throw new InvalidOperationException("Postal SOAP response exceeded the configured size limit.");

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, true, 8_192, leaveOpen: false);
        var output = new StringBuilder(Math.Min(maximumCharacters, 8_192));
        var buffer = new char[Math.Min(maximumCharacters + 1, 8_192)];
        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0) return output.ToString();
            if (output.Length + read > maximumCharacters)
                throw new InvalidOperationException("Postal SOAP response exceeded the configured size limit.");
            output.Append(buffer, 0, read);
        }
    }
}
