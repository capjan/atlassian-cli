using AtlassianCli.Tests;

int failures = 0;
List<(string Name, Func<Task> Run)> tests = [.. HttpTests.Cases(), .. ClientTests.Cases(), .. CliTests.Cases(), .. OutputTests.Cases()];
foreach ((string name, Func<Task> run) in tests)
{
    try
    {
        await run();
        Console.WriteLine($"PASS {name}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {name}: {exception}");
    }
}

Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed.");
return failures == 0 ? 0 : 1;
