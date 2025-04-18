using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Microsoft.Extensions.Configuration;
using System.Net.Http.Json;
using Serilog;
using Serilog.Sinks.SystemConsole.Themes;

var configuration = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .Build();


var redErrorTheme = new AnsiConsoleTheme(new Dictionary<ConsoleThemeStyle, string>
{
    [ConsoleThemeStyle.Text] = "\x1b[37m",              // Default: White
    [ConsoleThemeStyle.SecondaryText] = "\x1b[31m",
    [ConsoleThemeStyle.TertiaryText] = "\x1b[37m",
    [ConsoleThemeStyle.Invalid] = "\x1b[31m",           // Red for invalid
    [ConsoleThemeStyle.Null] = "\x1b[31m",              // Red for null
    [ConsoleThemeStyle.Name] = "\x1b[37m",
    [ConsoleThemeStyle.String] = "\x1b[32m",            // Green strings
    [ConsoleThemeStyle.Number] = "\x1b[37m",            // Yellow numbers
    [ConsoleThemeStyle.Boolean] = "\x1b[33m",
    [ConsoleThemeStyle.Scalar] = "\x1b[36m",
    [ConsoleThemeStyle.LevelVerbose] = "\x1b[37m",
    [ConsoleThemeStyle.LevelDebug] = "\x1b[37m",
    [ConsoleThemeStyle.LevelInformation] = "\x1b[36m",  // Cyan
    [ConsoleThemeStyle.LevelWarning] = "\x1b[33m",      // Yellow
    [ConsoleThemeStyle.LevelError] = "\x1b[31m",        // 🔴 RED
    [ConsoleThemeStyle.LevelFatal] = "\x1b[31m",        // 🔴 RED
});


Log.Logger = new LoggerConfiguration()
                .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}", theme: redErrorTheme)
                .WriteTo.File("logs/log.txt")
                .CreateLogger();





var factory = new ConnectionFactory()
{
    HostName = configuration["RabbitMQ:HostName"],
    Port = int.Parse(configuration["RabbitMQ:Port"] ?? "5672"), // Default RabbitMQ port is 5672
    UserName = configuration["RabbitMQ:UserName"], // e.g., "guest"
    Password = configuration["RabbitMQ:Password"], // e.g., "guest"
};

using var connection = factory.CreateConnection();
using var channel = connection.CreateModel();



Console.Clear();

var queueName = configuration["RabbitMQ:QueueName"];

if (string.IsNullOrWhiteSpace(queueName))
{

    Log.Error("❌ RabbitMQ QueueName is not configured. Exiting application.");
    return;
}

channel.QueueDeclare(queue: queueName,
                     durable: true,
                     exclusive: false,
                     autoDelete: false,
                     arguments: null);

var consumer = new EventingBasicConsumer(channel);


consumer.Received += async (model, ea) =>
{
    var body = ea.Body.ToArray();
    var message = Encoding.UTF8.GetString(body);

    Console.ForegroundColor = ConsoleColor.Yellow;
    Log.Information(" ");
    Log.Information("📨 Received Message: " + message);
    Log.Information(" ");

    

    try
    {

        // Step 1: Deserialize outer wrapper
        using var doc = JsonDocument.Parse(message);
        if (!doc.RootElement.TryGetProperty("queuedata", out var queuedataElement))
        {
            Log.Warning("⚠️ 'queuedata' field missing in received message. Skipping.\n{ex}");
            return;
        }

        var queuedataJson = queuedataElement.GetString();
        if (string.IsNullOrWhiteSpace(queuedataJson))
        {
            Log.Warning("⚠️ 'queuedata' field is empty. Skipping.");
            return;
        }

        try
        {
            using var innerDoc = JsonDocument.Parse(queuedataJson);

            var prettyJson = JsonSerializer.Serialize(innerDoc.RootElement, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            Log.Information("📦 Extracted Queuedata as JSON Object:\n" + prettyJson);
        }
        catch (JsonException je)
        {
            Log.Warning(je, "⚠️ Could not parse queuedata as JSON object.");
        }

        //Log.Information("📦 Extracted Queuedata JSON: " + queuedataJson);

        // Step 2: Deserialize inner 'queuedata' JSON
        ScheduledMessage? scheduledMessage;

        try
        {
            scheduledMessage = JsonSerializer.Deserialize<ScheduledMessage>(queuedataJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            Log.Information(scheduledMessage.ToString()); 
        }
        catch (JsonException jsonEx)
        {
            Console.ForegroundColor = ConsoleColor.Blue;
            Log.Error(jsonEx, "❌ Invalid JSON structure received. Skipping message.");
            return;
        }

        if (scheduledMessage == null)
        {
            Log.Warning("⚠️ Null ScheduledMessage after deserialization. Skipping.");
            return;
        }

        if (scheduledMessage.email != null)
        {
            var email = scheduledMessage.email;

            if (email.to == null || !email.to.Any(t => !string.IsNullOrWhiteSpace(t)) ||
                string.IsNullOrWhiteSpace(email.subject) ||
                email.body == null || !email.body.Any(t => !string.IsNullOrWhiteSpace(t)))
            {
                Log.Warning("❌ Missing required Email fields (To / Subject / Body). Skipping.");
            }
            else
            {
                // 👉 Inject redis into email
                if (scheduledMessage.redis != null)
                {
                    Log.Information("🔍 RedisConfig before injection: " + JsonSerializer.Serialize(scheduledMessage.redis));
                    email.redis = scheduledMessage.redis;
                    Log.Information("🔗 Redis details injected into Email.");
                }

                // Log the full email request as JSON
                var emailJson = JsonSerializer.Serialize(email, new JsonSerializerOptions
                {
                    WriteIndented = true // Prettifies the JSON for better readability in logs
                });

                Log.Information("📧 Final Email Request JSON:\n" + emailJson);

                Log.Information("Email API URL " + configuration["ApiUrls:Email"]);

                await CallEmailApi(email);
            }
        }

        if (scheduledMessage.sms != null)
        {
            var sms = scheduledMessage.sms;

            if (sms.mobileno == null || !sms.mobileno.Any(t => !string.IsNullOrWhiteSpace(t)) ||
                sms.message == null || !sms.message.Any(m => !string.IsNullOrWhiteSpace(m)))
            {
                Log.Warning("❌ Missing required SMS fields (MobileNo / Message). Skipping SMS call.");
            }
            else
            {
                // Log the full sms request as JSON
                var smsJson = JsonSerializer.Serialize(sms, new JsonSerializerOptions
                {
                    WriteIndented = true // Prettifies the JSON for better readability in logs
                });

                Log.Information("📧 Final SMS Request JSON:\n" + smsJson);
                Log.Information("SMS API URL " + configuration["ApiUrls:Sms"]);

                await CallSmsApi(sms);
            }
        }

        // Add WhatsApp, Teams etc. similarly

        channel.BasicAck(deliveryTag: ea.DeliveryTag, multiple: false);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "🔥 Unexpected error while processing message.");
    }
};

channel.BasicConsume(queue: queueName,
                     autoAck: false,
                     consumer: consumer);

Console.WriteLine("📥 Listening for messages... Press [Enter] to exit.");
Console.ReadLine();

async Task CallEmailApi(EmailMessage emailRequest)
{
    try
    {
        using var client = new HttpClient();
        string? apiUrl = configuration["ApiUrls:Email"];

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            Log.Warning("⚠️ Email API URL is missing in configuration.");
            return;
        }

        
        if (emailRequest.redis == null)
        {
                
            Log.Warning("⚠️ Redis object is null in EmailMessage.");
        }

        var response = await client.PostAsJsonAsync(apiUrl, emailRequest);

        Console.WriteLine(response);

        if (response.IsSuccessStatusCode)
        {
            Log.Information("✅ Email API call succeeded.");
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Warning($"❌ Email API failed. Status Code: {response.StatusCode}, Details: {errorContent}");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "🔥 Exception while calling Email API.");
    }
}

async Task CallSmsApi(SMSMessage sms)
{
    try
    {
        using var client = new HttpClient();
        string? apiUrl = configuration["ApiUrls:Sms"];

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            Log.Warning("⚠️ SMS API URL is missing in configuration.");
            return;
        }

        var json = JsonSerializer.Serialize(sms);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await client.PostAsync(apiUrl, content);

        Console.WriteLine(response);

        if (response.IsSuccessStatusCode)
        {
            Log.Information("✅ SMS API call succeeded.");
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            Log.Warning($"❌ SMS API failed. Status Code: {response.StatusCode}, Details: {errorContent}");
        }
    }
    catch (Exception ex)
    {
        Log.Error(ex, "🔥 Exception while calling SMS API.");
    }
}




