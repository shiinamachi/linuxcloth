namespace LinuxCloth.Wsb;

public enum WsbParseMode
{
    Normal,

    // This mode only makes unsafe settings visible for inspection. Callers must
    // not execute the parsed configuration without separate user approval.
    AdvancedInspection,
}
