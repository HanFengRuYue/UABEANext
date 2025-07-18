namespace UABEANext4.Logic;

public class ScanOptions
{
    public bool IncludeSubdirectories { get; set; } = true;
    public bool ScanCommonUnityDirectories { get; set; } = true;
    public bool ValidateFileTypes { get; set; } = true;
    public bool SkipSmallFiles { get; set; } = true;
} 