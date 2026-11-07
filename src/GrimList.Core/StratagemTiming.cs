using System.Text.RegularExpressions;

namespace GrimList.Core;

public enum StratagemTurn { Unknown, Either, Yours, Opponents }

public static class StratagemTiming
{
    public static StratagemTurn FromText(string text)
    {
        // Only the activation window determines the colour, never the duration of the effect.
        var when = Regex.Match(text, @"\bWHEN\s*:\s*(.*?)(?=\b(?:TARGET|EFFECT|RESTRICTIONS|REMINDER)\s*:|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!when.Success) return StratagemTurn.Unknown;
        var timing = Regex.Replace(when.Groups[1].Value.Replace('’', '\''), @"\s+", " ").ToLowerInvariant();
        // A shared phase suffix keeps its owner, including "Movement/Charge phase".
        timing = Regex.Replace(timing,
            @"\b(?<owner>your opponent's|opponent's|your|either player's) (?<first>command|movement|shooting|charge|fight)(?: or |\s*/\s*)(?<second>command|movement|shooting|charge|fight) phase\b",
            "${owner} ${first} phase or ${owner} ${second} phase");
        var turns = Regex.Matches(timing, @"\b(?<owner>your opponent's|opponent's|your|either player's|either|both players'|each player's) turns?\b");
        var phases = Regex.Matches(timing, @"\b(?:(?<owner>your opponent's|opponent's|your|either player's|either|each player's|any|the) )?(?:command|movement|shooting|charge|fight|any) phase\b");
        var yours = false; var opponents = false;
        void Include(string owner)
        {
            if (owner is "your opponent's" or "opponent's") opponents = true;
            else if (owner == "your") yours = true;
            else { yours = true; opponents = true; }
        }
        // An explicit turn restriction also scopes otherwise unqualified phases.
        foreach (Match turn in turns) Include(turn.Groups["owner"].Value);
        foreach (Match phase in phases)
        {
            var owner = phase.Groups["owner"].Value;
            if (turns.Count == 0 || owner is "your" or "your opponent's" or "opponent's") Include(owner);
        }
        return (yours, opponents) switch { (true, true) => StratagemTurn.Either, (true, false) => StratagemTurn.Yours,
            (false, true) => StratagemTurn.Opponents, _ => StratagemTurn.Unknown };
    }
}
