namespace AtlassianCli.Http;

public sealed record PageRequest(int Start = 0, int Limit = 100, bool All = false)
{
    public void Validate()
    {
        if (Start < 0 || Limit is < 1 or > 10000)
        {
            throw new ArgumentException("--start must be non-negative and --limit must be between 1 and 10000.");
        }
    }
}
