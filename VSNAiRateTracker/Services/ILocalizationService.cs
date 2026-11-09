using System;
using System.Collections.Generic;

namespace VSNAiRateTracker.Services;

public interface ILocalizationService
{
    string CurrentLanguage { get; set; }
    IReadOnlyList<string> SupportedLanguages { get; }
    string GetString(string key);
    string GetString(string key, params object[] args);
    event EventHandler? LanguageChanged;
}
