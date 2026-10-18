using System.Text.Json;
using Elephentity.GraphQL.Http;
using Elephentity.Runtime.Gateway;
using Elephentity.Runtime.Memory;
using Elephentity.Runtime.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Golden.GraphQL.Tests;

/// <summary>
/// The generated manifests assembled with <c>Elephentity.GraphQL</c> on the memory driver, beside the entities
/// <c>eleph-gen-csharp</c> made from the same IR, and executed: create, read, page, an action, a published query, delete.
/// </summary>
public sealed class ExecutionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static async Task<JsonElement> Run(ServiceProvider services, string query, object? variables = null)
    {
        var executor = services.GetRequiredService<ElephentityGraphQLExecutor>();
        var request = new global::GraphQL.Transport.GraphQLRequest
        {
            Query = query,
            Variables = variables is null ? null : executor.Serializer.Deserialize<global::GraphQL.Inputs>(JsonSerializer.Serialize(variables)),
        };
        var json = await executor.ExecuteToJsonAsync(request, services, Token);
        var result = JsonDocument.Parse(json).RootElement.Clone();

        Assert.False(result.TryGetProperty("errors", out var errors), errors.ToString());

        return result.GetProperty("data");
    }

    private static ServiceProvider Book()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IStorageAdaptor>(new MemoryAdaptor());
        services.AddSingleton<Golden.Book.Contract.IBookSearchQuery, Golden.Book.Stubs.Search>();
        services.AddSingleton<Golden.Book.Contract.IBookRenameAction, Golden.Book.Stubs.Rename>();
        Golden.Book.Wiring.AddTestEntities(services);
        services.AddElephentityGraphQL(Golden.Book.GraphQL.GraphQLManifestFactory.Create());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task ABookIsCreatedReadPagedRenamedSearchedAndDeletedThroughGraphQL()
    {
        using var services = Book();

        for (var n = 1; n <= 3; n++)
        {
            await Run(services, "mutation($t: String!) { createBook(input: { title: $t, status: DRAFT }) { book { id } } }", new { t = $"Book {n}" });
        }

        var page = (await Run(services, "{ books(first: 2) { totalCount nodes { title status } pageInfo { hasNextPage endCursor } } }")).GetProperty("books");

        Assert.Equal(3, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(["Book 1", "Book 2"], page.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("title").GetString()));
        Assert.Equal("DRAFT", page.GetProperty("nodes")[0].GetProperty("status").GetString());
        Assert.True(page.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var next = (await Run(
            services,
            "query($a: String) { books(first: 2, after: $a) { nodes { title } pageInfo { hasNextPage } } }",
            new { a = page.GetProperty("pageInfo").GetProperty("endCursor").GetString() })).GetProperty("books");

        Assert.Equal(["Book 3"], next.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("title").GetString()));
        Assert.False(next.GetProperty("pageInfo").GetProperty("hasNextPage").GetBoolean());

        var id = (await Run(services, "{ books(first: 1) { nodes { id } } }")).GetProperty("books").GetProperty("nodes")[0].GetProperty("id").GetString();
        var renamed = (await Run(services, "mutation($id: ID!) { renameBook(input: { id: $id, title: \"Renamed\" }) { book { title } } }", new { id }))
            .GetProperty("renameBook").GetProperty("book");

        Assert.Equal("Renamed", renamed.GetProperty("title").GetString());
        Assert.Equal("Renamed", (await Run(services, "query($id: ID!) { book(id: $id) { title } }", new { id })).GetProperty("book").GetProperty("title").GetString());
        Assert.Equal(1, (await Run(services, "{ searchBooks(where: { text: \"Renamed\" }) { totalCount nodes { title } } }")).GetProperty("searchBooks").GetProperty("totalCount").GetInt32());

        var deleted = (await Run(services, "mutation($id: ID!) { deleteBook(input: { id: $id }) { deletedId } }", new { id })).GetProperty("deleteBook");

        Assert.Equal(id, deleted.GetProperty("deletedId").GetString());
        Assert.Equal(2, (await Run(services, "{ books { totalCount } }")).GetProperty("books").GetProperty("totalCount").GetInt32());
    }

    private static ServiceProvider Relations()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IStorageAdaptor>(new MemoryAdaptor());
        services.AddSingleton<Golden.Relations.Types.IMoneyReadProcessor, Golden.Relations.Stubs.MoneyReader>();
        services.AddSingleton<Golden.Relations.Types.IMoneyWriteProcessor, Golden.Relations.Stubs.MoneyWriter>();
        services.AddSingleton<Golden.Relations.Contract.IPostPublishAction, Golden.Relations.Stubs.Publish>();
        services.AddSingleton<Golden.Relations.Contract.IPostPublishedQuery, Golden.Relations.Stubs.Published>();
        services.AddSingleton<Golden.Relations.Contract.IPostAuditSideEffect, Golden.Relations.Stubs.Effect>();
        services.AddSingleton<Golden.Relations.Contract.IPostReindexSideEffect, Golden.Relations.Stubs.Effect>();
        services.AddSingleton<Golden.Relations.Contract.IPostPriceVerifier, Golden.Relations.Stubs.Price>();
        services.AddSingleton<Golden.Relations.Pattern.Contract.IAuditableOwnerReadPolicy, Golden.Relations.Stubs.Allow>();
        services.AddSingleton<Golden.Relations.Contract.IPostReporterReadPolicy, Golden.Relations.Stubs.Allow>();
        services.AddSingleton<Golden.Relations.Contract.IPostNoBackwardsWritePolicy, Golden.Relations.Stubs.Allow>();
        Golden.Relations.Wiring.AddFixtureEntities(services);
        services.AddElephentityGraphQL(Golden.Relations.GraphQL.GraphQLManifestFactory.Create());

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task EdgesInEveryShapeResolveAndAPublishedQueryFiltersByTheActionsEffect()
    {
        using var services = Relations();

        var author = (await Run(services, "mutation { createAuthor(input: { name: \"Ada\" }) { author { id } } }")).GetProperty("createAuthor").GetProperty("author").GetProperty("id").GetString();
        var tag = (await Run(services, "mutation { createTag(input: { label: \"news\" }) { tag { id } } }")).GetProperty("createTag").GetProperty("tag").GetProperty("id").GetString();
        var post = (await Run(
            services,
            "mutation($tag: ID!, $author: ID!) { createPost(input: { title: \"Hello\", status: DRAFT, slug: \"hello\", visibility: PUBLIC, tags: [$tag], featuredAuthor: $author }) { post { id } } }",
            new { tag, author })).GetProperty("createPost").GetProperty("post").GetProperty("id").GetString();
        await Run(services, "mutation($c: String!, $author: ID!) { createComment(input: { body: $c, author: $author }) { comment { id } } }", new { c = "Nice", author });

        var read = (await Run(
            services,
            "query($id: ID!) { post(id: $id) { title tags { nodes { label } } featuredAuthor { name } } }",
            new { id = post })).GetProperty("post");

        Assert.Equal(["news"], read.GetProperty("tags").GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("label").GetString()));
        Assert.Equal("Ada", read.GetProperty("featuredAuthor").GetProperty("name").GetString());

        // The inverse of Comment.author is a connection on Author.
        var comments = (await Run(services, "query($id: ID!) { author(id: $id) { comments { nodes { body } } } }", new { id = author })).GetProperty("author").GetProperty("comments");

        Assert.Equal(["Nice"], comments.GetProperty("nodes").EnumerateArray().Select(node => node.GetProperty("body").GetString()));

        Assert.Equal(0, (await Run(services, "{ publishedPosts { totalCount } }")).GetProperty("publishedPosts").GetProperty("totalCount").GetInt32());

        await Run(services, "mutation($id: ID!) { publishPost(input: { id: $id }) { post { id } } }", new { id = post });

        Assert.Equal(1, (await Run(services, "{ publishedPosts { totalCount } }")).GetProperty("publishedPosts").GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task TheClogSchemaAssemblesAndAnEmptyGraphAnswers()
    {
        var services = new ServiceCollection();

        services.AddSingleton<IStorageAdaptor>(new MemoryAdaptor());
        services.AddSingleton<Golden.Clog.Contract.IItemSearchQuery, Golden.Clog.Stubs.Search>();
        services.AddSingleton<Golden.Clog.Contract.IItemDiscontinueAction, Golden.Clog.Stubs.Discontinue>();
        services.AddSingleton<Golden.Clog.Contract.IItemDefaultExpiryUnitVerifier, Golden.Clog.Stubs.Verify>();
        services.AddSingleton<Golden.Clog.Contract.IItemDefaultExpiryValueVerifier, Golden.Clog.Stubs.Verify>();
        services.AddSingleton<Golden.Clog.Contract.IInventorySiteAvailabilitySideEffect, Golden.Clog.Stubs.Availability>();
        services.AddSingleton<Golden.Clog.Contract.IItemStaffWritePolicy, Golden.Clog.Stubs.Allow>();
        services.AddSingleton<Golden.Clog.Pattern.Contract.IClogPostSignedInReadPolicy, Golden.Clog.Stubs.Allow>();
        Golden.Clog.Wiring.AddClogEntities(services);
        services.AddElephentityGraphQL(Golden.Clog.GraphQL.GraphQLManifestFactory.Create());

        using var provider = services.BuildServiceProvider();

        var location = (await Run(provider, "mutation { createClogLocation(input: { name: \"Pantry\" }) { clogLocation { name } } }")).GetProperty("createClogLocation");
        var empty = await Run(provider, "{ clogItems { totalCount } clogItemSearch(where: { term: \"x\" }) { totalCount } clogLocations { nodes { name } } }");

        Assert.Equal("Pantry", location.GetProperty("clogLocation").GetProperty("name").GetString());
        Assert.Equal(0, empty.GetProperty("clogItems").GetProperty("totalCount").GetInt32());
        Assert.Equal(0, empty.GetProperty("clogItemSearch").GetProperty("totalCount").GetInt32());
        Assert.Equal("Pantry", empty.GetProperty("clogLocations").GetProperty("nodes")[0].GetProperty("name").GetString());
    }
}
