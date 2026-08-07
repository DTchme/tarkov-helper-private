using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;

var client = new HttpClient();
var response = await client.GetStringAsync("https://tarkov-market.com/api/be/quests/list");
Console.WriteLine($"Response length: {response.Length}");

// Parse wrapper
using var doc = JsonDocument.Parse(response);
var quests = doc.RootElement.GetProperty("quests").GetString();
Console.WriteLine($"Encoded quests length: {quests.Length}");

// Decode
var processed = quests.Substring(0, 5) + quests.Substring(10);
Console.WriteLine($"Processed length: {processed.Length}");

var bytes = Convert.FromBase64String(processed);
var urlEncoded = Encoding.UTF8.GetString(bytes);
Console.WriteLine($"URL encoded length: {urlEncoded.Length}");

var json = Uri.UnescapeDataString(urlEncoded);
Console.WriteLine($"Decoded JSON length: {json.Length}");
Console.WriteLine($"First 500 chars: {json.Substring(0, Math.Min(500, json.Length))}");
