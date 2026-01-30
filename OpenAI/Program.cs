using OpenAI.Chat;

string? apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("Missing OPENAI_API_KEY. Set it and re-run the app.");
    return;
}

ChatClient client = new(model: "gpt-4.1", apiKey: apiKey);
ChatCompletion completion = client.CompleteChat("Say 'this is a test.'");

Console.WriteLine($"[ASSISTANT]: {completion.Content[0].Text}");
