using System.Text.Json.Serialization;
using System.Collections.Generic;

public class ScheduledMessage
{
    public EmailMessage email { get; set; } = new();
    public SMSMessage sms { get; set; } = new();
    public RedisConfig redis { get; set; } = new();  // Proper initialization
}

public class EmailMessage
{
    public List<string> to { get; set; } = new();  // Initialize List<string>
    public string subject { get; set; } = string.Empty;  // Default to empty string
    public List<string> body { get; set; } = new();  // Initialize List<string>
    public RedisConfig redis { get; set; } = new();  // Initialize RedisConfig
}

public class RedisConfig
{
    public string redisserver { get; set; } = string.Empty;  // Default to empty string
    public string redisport { get; set; } = string.Empty;    // Default to empty string
    public string redispwd { get; set; } = string.Empty;     // Default to empty string
    public string emailconfigkey { get; set; } = string.Empty; // Default to empty string
}

public class SMSMessage
{
    public List<string> mobileno { get; set; } = new();  // Initialize List<string>
    public List<string> message { get; set; } = new();  // Changed to List<string>
    //public RedisConfig redis { get; set; } = new();  // Initialize RedisConfig
}
