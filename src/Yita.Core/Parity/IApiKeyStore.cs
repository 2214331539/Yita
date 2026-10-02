namespace Yita.Settings;

internal interface IApiKeyStore
{
    string ReadApiKey();
    void SaveApiKey(string apiKey);
}
