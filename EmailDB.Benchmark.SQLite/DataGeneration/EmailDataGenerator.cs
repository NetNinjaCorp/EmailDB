using Bogus;
using EmailDB.Benchmark.SQLite.Abstractions;

namespace EmailDB.Benchmark.SQLite.DataGeneration;

public class EmailDataGenerator
{
    private readonly Faker<BenchmarkEmail> _faker;
    private readonly Random _random;

    public EmailDataGenerator(int seed = 42)
    {
        _random = new Random(seed);
        Randomizer.Seed = new Random(seed);

        _faker = new Faker<BenchmarkEmail>()
            .RuleFor(e => e.Id, f => Guid.NewGuid().ToString())
            .RuleFor(e => e.Subject, f => f.Lorem.Sentence())
            .RuleFor(e => e.Body, f => f.Lorem.Paragraphs(f.Random.Int(1, 5)))
            .RuleFor(e => e.From, f => f.Internet.Email())
            .RuleFor(e => e.To, f => new List<string> { f.Internet.Email(), f.Internet.Email() })
            .RuleFor(e => e.Cc, f => f.Random.Int(0, 3) > 0
                ? new List<string> { f.Internet.Email() }
                : new List<string>())
            .RuleFor(e => e.Bcc, f => f.Random.Int(0, 2) > 0
                ? new List<string> { f.Internet.Email() }
                : new List<string>())
            .RuleFor(e => e.SentDate, f => f.Date.Recent(30))
            .RuleFor(e => e.FolderPath, f => f.PickRandom("Inbox", @"Inbox\Work", @"Inbox\Personal", "Sent", "Drafts"));
    }

    public BenchmarkEmail Generate() => _faker.Generate();

    public List<BenchmarkEmail> Generate(int count) => _faker.Generate(count);

    public List<BenchmarkEmail> GenerateForFolder(int count, string folderPath)
    {
        return _faker.Generate(count).Select(e =>
        {
            e.FolderPath = folderPath;
            return e;
        }).ToList();
    }
}
