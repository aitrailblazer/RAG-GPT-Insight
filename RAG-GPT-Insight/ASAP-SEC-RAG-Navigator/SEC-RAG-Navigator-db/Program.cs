using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Fluent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.AzureCosmosDBNoSQL;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.TemplateEngine;
using Microsoft.SemanticKernel.Prompty;
using Microsoft.SemanticKernel.Data;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.Extensions.Options;
using Cosmos.Copilot.Models;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using OpenAI.Chat;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
class Program
{

    // The Azure Cosmos DB client instance
    private CosmosClient cosmosClient = null!;
    // The Azure Cosmos DB database instance
    private Database database = null!;
    // The Azure Cosmos DB container instance
    private Container container = null!;

    // 
    /// <summary>
    /// The main entry point for the application.
    /// </summary> 
    /// <param name="args">The command-line arguments.</param>
    /// <returns>A task that represents the asynchronous operation.</returns>
    static async Task Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return;
        }

        using IHost host = CreateHostBuilder(args).Build();

        var navigatorService = host.Services.GetRequiredService<SEC_RAG_NavigatorService>();
        await navigatorService.ExecuteAsync(args);
    }


    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SEC-RAG-Navigator create-container <containerName>");
        Console.WriteLine("  SEC-RAG-Navigator list-containers");
        Console.WriteLine("  SEC-RAG-Navigator rag-chat-service");
        Console.WriteLine("  SEC-RAG-Navigator knowledge-base-search \"<promptText>\"");
        Console.WriteLine("  SEC-RAG-Navigator phi-3.5-moe-instruct");
    }


    static IHostBuilder CreateHostBuilder(string[] args) =>
        Host.CreateDefaultBuilder(args)
            .ConfigureServices((context, services) =>
            {
                // Add logging
                services.AddLogging(configure => configure.AddConsole());

                // Register CosmosClient
                services.AddSingleton<CosmosClient>(provider =>
                {
                    string azureCosmosDbEndpointUri = GetEnvironmentVariable("COSMOS_DB_ENDPOINT");
                    string primaryKey = GetEnvironmentVariable("COSMOS_DB_PRIMARY_KEY");

                    return new CosmosClientBuilder(azureCosmosDbEndpointUri, primaryKey)
                        .WithApplicationName("SEC-RAG-Navigator")
                        .Build();
                });

                // Register SEC_RAG_NavigatorService
                services.AddScoped<SEC_RAG_NavigatorService>();
                services.AddScoped<CosmosDbServiceWorking>(provider =>
                {
                    // Resolve dependencies
                    var cosmosClient = provider.GetRequiredService<CosmosClient>();
                    string databaseId = GetEnvironmentVariable("COSMOS_DB_DATABASE_ID");
                    var logger = provider.GetRequiredService<ILogger<CosmosDbServiceWorking>>();
                    var ragChatService = provider.GetRequiredService<RAGChatService<string>>(); // Resolve RAGChatService
                    var chatService = provider.GetRequiredService<ChatService>(); // Resolve ChatService

                    // Return an instance of CosmosDbServiceWorking with all dependencies
                    return new CosmosDbServiceWorking(cosmosClient, databaseId, logger, ragChatService, chatService);
                });


                // Register SemanticKernelService
                RegisterSemanticKernelService(services);

                // Register CosmosDbService
                services.AddSingleton<CosmosDbService>((provider) =>
                {
                    var cosmosDbOptions = provider.GetRequiredService<IOptions<CosmosDb>>();
                    if (cosmosDbOptions is null)
                    {
                        throw new ArgumentException($"{nameof(IOptions<CosmosDb>)} was not resolved through dependency injection.");
                    }
                    else
                    {
                        var logger = provider.GetRequiredService<ILogger<CosmosDbService>>(); // Retrieve the logger
                        string azureCosmosDbEndpointUri = GetEnvironmentVariable("COSMOS_DB_ENDPOINT");
                        string AzureCosmosDBNoSQLDatabaseName = "asapdb";
                        string knowledgeBaseContainerName = "secrag"; // rag

                        return new CosmosDbService(
                            endpoint: azureCosmosDbEndpointUri ?? string.Empty,
                            databaseName: AzureCosmosDBNoSQLDatabaseName ?? string.Empty,
                            knowledgeBaseContainerName: knowledgeBaseContainerName ?? string.Empty,
                            logger: logger // Pass the logger to the constructor
                        );
                    }
                });

                // Create a cancellation token and source to pass to the application service to allow them
                // to request a graceful application shutdown.
                CancellationTokenSource appShutdownCancellationTokenSource = new();
                CancellationToken appShutdownCancellationToken = appShutdownCancellationTokenSource.Token;
                services.AddKeyedSingleton("AppShutdown", appShutdownCancellationTokenSource);

                // Register Semantic Kernel and related services
                RegisterKernelServices(context, services);
            });

    // 
    /// <summary>
    /// Registers the SemanticKernelService with the provided <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the service to.</param>
    /// <remarks>
    /// This method fetches the necessary environment variables for configuring the SemanticKernelService,
    /// including the endpoint, API key, and deployment names for completion and embedding models.
    /// If the deployment names are not provided, default values are used.
    /// The service is registered with a scoped lifetime.
    /// </remarks>
    static void RegisterSemanticKernelService(IServiceCollection services)
    {
        // Fetch environment variables for Semantic Kernel
        string endpoint = GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        string apiKey = GetEnvironmentVariable("AZURE_OPENAI_KEY");
        string completionDeploymentName = GetEnvironmentVariable("AZURE_OPENAI_COMPLETION_DEPLOYMENT_NAME") ?? "gpt-4o";
        string embeddingDeploymentName = GetEnvironmentVariable("AZURE_OPENAI_EMBEDDING_DEPLOYMENT_NAME") ?? "text-embedding-3-large";
        int dimensions = 3072;

        // Register SemanticKernelService
        services.AddScoped<SemanticKernelService>(provider =>
        {
            var logger = provider.GetRequiredService<ILogger<SemanticKernelService>>();

            return new SemanticKernelService(
                endpoint: endpoint,
                completionDeploymentName: completionDeploymentName,
                embeddingDeploymentName: embeddingDeploymentName,
                apiKey: apiKey,
                dimensions: dimensions,
                logger: logger
            );
        });
    }

    /// <summary>
    /// Registers kernel services and configurations for the application.
    /// </summary>
    /// <param name="context">The host builder context.</param>
    /// <param name="services">The service collection to which services are added.</param>
    static void RegisterKernelServices(HostBuilderContext context, IServiceCollection services)
    {
        string azureOpenAIChatDeploymentName = "gpt-4o";
        string azureEmbeddingDeploymentName = "text-embedding-3-large";
        string azureOpenAIEndpoint = GetEnvironmentVariable("AZURE_OPENAI_ENDPOINT");
        string azureOpenAIKey = GetEnvironmentVariable("AZURE_OPENAI_KEY");
        string azureCosmosDBNoSQLConnectionString = GetEnvironmentVariable("COSMOS_DB_CONNECTION_STRING");
        string azureCosmosDBNoSQLDatabaseName = GetEnvironmentVariable("COSMOS_DB_DATABASE_ID");
        string ragCollectionName = "ragcontent";
        int azureEmbeddingDimensions = 3072;

        var kernelBuilder = services.AddKernel();

        kernelBuilder.AddAzureOpenAIChatCompletion(
            azureOpenAIChatDeploymentName,
            azureOpenAIEndpoint,
            azureOpenAIKey);

        kernelBuilder.AddAzureOpenAITextEmbeddingGeneration(
            azureEmbeddingDeploymentName,
            azureOpenAIEndpoint,
            azureOpenAIKey,
            dimensions: azureEmbeddingDimensions);

        kernelBuilder.AddAzureCosmosDBNoSQLVectorStoreRecordCollection<TextSnippet<string>>(
            ragCollectionName,
            azureCosmosDBNoSQLConnectionString,
            azureCosmosDBNoSQLDatabaseName);

        RegisterServices<string>(services, kernelBuilder);

        services.AddScoped<ChatService>((provider) =>
        {
            var cosmosDbService = provider.GetRequiredService<CosmosDbService>();
            var semanticKernelService = provider.GetRequiredService<SemanticKernelService>();

            var logger = provider.GetRequiredService<ILogger<ChatService>>();

            return new ChatService(
                cosmosDbService: cosmosDbService,
                semanticKernelService: semanticKernelService,
                maxConversationTokens: "4000",
                cacheSimilarityScore: "0.5",
                logger: logger
            );
        });
    }


    /// <summary>
    /// Registers services and configurations for the application.
    /// </summary>
    /// <typeparam name="TKey">The type of the key used in the text snippets.</typeparam>
    /// <param name="services">The service collection to which services are added.</param>
    /// <param name="kernelBuilder">The kernel builder used to configure the kernel.</param>
    static void RegisterServices<TKey>(IServiceCollection services, IKernelBuilder kernelBuilder)
        where TKey : notnull
    {
        // Add vector store text search with custom mappers
        kernelBuilder.AddVectorStoreTextSearch<TextSnippet<TKey>>(
            new TextSearchStringMapper((result) =>
            {
                if (result is TextSnippet<TKey> castResult)
                {
                    return castResult.Text ?? string.Empty;
                }
                throw new InvalidCastException("Result is not of type TextSnippet<TKey>.");
            }),
            new TextSearchResultMapper((result) =>
            {
                if (result is TextSnippet<TKey> castResult)
                {
                    return new TextSearchResult(value: castResult.Text ?? string.Empty)
                    {
                        Name = castResult.ReferenceDescription ?? "No Description",
                        Link = castResult.ReferenceLink ?? "No Link"
                    };
                }
                throw new InvalidCastException("Result is not of type TextSnippet<TKey>.");
            }));

        // Register the data loader as a singleton service
        services.AddSingleton<IDataLoader, DataLoader<TKey>>();

        // Register the RAG chat service as a scoped service
        services.AddScoped<RAGChatService<TKey>>();
    }
    static string GetEnvironmentVariable(string variableName)
    {
        return Environment.GetEnvironmentVariable(variableName)
            ?? throw new ArgumentNullException(variableName, $"{variableName} is not set in environment variables.");
    }
}

public class SEC_RAG_NavigatorService
{
    private readonly CosmosDbServiceWorking _cosmosDbServiceWorking;
    private readonly ChatService _chatService;
    private readonly ILogger<SEC_RAG_NavigatorService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="SEC_RAG_NavigatorService"/> class.
    /// </summary>
    /// <param name="cosmosDbServiceWorking">The Cosmos DB service instance.</param>
    /// <param name="chatService">The chat service instance.</param>
    /// <param name="logger">The logger instance.</param>
    public SEC_RAG_NavigatorService(
        CosmosDbServiceWorking cosmosDbServiceWorking,
        ChatService chatService,
        ILogger<SEC_RAG_NavigatorService> logger)
    {
        _cosmosDbServiceWorking = cosmosDbServiceWorking ?? throw new ArgumentNullException(nameof(cosmosDbServiceWorking));
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes the specified command asynchronously based on the provided arguments.
    /// </summary>
    /// <param name="args">The command-line arguments.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task ExecuteAsync(string[] args)
    {
        string command = args[0].ToLower();

        try
        {
            if (command == "create-container" && args.Length >= 2)
            {
                string containerName = args[1];
                List<string> partitionKeyPaths = new List<string> { "/tenantId", "/userId", "/categoryId" };
                await _cosmosDbServiceWorking.CreateDatabaseAsync();
                await _cosmosDbServiceWorking.CreateContainerAsync(
                    containerName,
                    "/vectors",
                    partitionKeyPaths,
                    new List<string> { "/*" },
                    3072
                );
            }
            else if (command == "list-containers")
            {
                await _cosmosDbServiceWorking.ListDatabasesAndContainersAsync();
            }
            else if (command == "rag-chat-service")
            {
                string tenantID = "1234";
                string userID = "5678";
                await _cosmosDbServiceWorking.HandleInputFileFromPath(tenantID, userID);
            }
            else if (command == "knowledge-base-search" && args.Length >= 2)
            {
                // Retrieve the prompt text (e.g., "Risk factors")
                string promptText = args[1];

                // Example tenant and user ID
                string tenantID = "1234";
                string userID = "5678";

                // Call the knowledge base search handler
                await _cosmosDbServiceWorking.HandleKnowledgeBaseCompletionCommandAsync(
                    tenantId: tenantID,
                    userId: userID,
                    categoryId: "Document", // Example category
                    promptText: promptText,
                    similarityScore: 0.7 // Default similarity score
                );
            }
            else if (command == "phi-3.5-moe-instruct")
            {
                await _cosmosDbServiceWorking.HandlePhi35MoEInstructCommandAsyncs();
            }
            else
            {
                Console.WriteLine("Invalid command or missing arguments. Use one of the following:");
                PrintUsage();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while executing SEC-RAG-Navigator.");
            Console.WriteLine($"\nError: {ex.Message}");
        }
    }

    /// <summary>
    /// Prints the usage instructions for the SEC-RAG-Navigator application.
    /// </summary>
    static void PrintUsage()
    {
        Console.WriteLine("Usage:");
        Console.WriteLine("  SEC-RAG-Navigator create-container <containerName>");
        Console.WriteLine("  SEC-RAG-Navigator list-containers");
        Console.WriteLine("  SEC-RAG-Navigator rag-chat-service");
        Console.WriteLine("  SEC-RAG-Navigator knowledge-base-search \"<promptText>\"");
        Console.WriteLine("  SEC-RAG-Navigator phi-3.5-moe-instruct");
    }
}

public class CosmosDbServiceWorking
{
    private readonly CosmosClient _cosmosClient;
    private readonly string _databaseId;
    private readonly ILogger<CosmosDbServiceWorking> _logger;
    private readonly RAGChatService<string> _ragChatService;
    private readonly ChatService _chatService;

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosDbServiceWorking"/> class.
    /// </summary>
    /// <param name="cosmosClient">The Cosmos DB client.</param>
    /// <param name="databaseId">The ID of the database.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="ragChatService">The RAG chat service instance.</param>
    /// <param name="chatService">The chat service instance.</param>
    public CosmosDbServiceWorking(
        CosmosClient cosmosClient,
        string databaseId,
        ILogger<CosmosDbServiceWorking> logger,
        RAGChatService<string> ragChatService,
        ChatService chatService)
    {
        _cosmosClient = cosmosClient ?? throw new ArgumentNullException(nameof(cosmosClient));
        _databaseId = databaseId ?? throw new ArgumentNullException(nameof(databaseId));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _ragChatService = ragChatService ?? throw new ArgumentNullException(nameof(ragChatService));
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
    }

    /// <summary>
    /// Creates a database in the Cosmos DB account if it does not already exist.
    /// </summary>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task CreateDatabaseAsync()
    {
        Database database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseId);
        _logger.LogInformation($"Database created or exists: {database.Id}");
    }

    /// <summary>
    /// Creates a container in the Cosmos DB with specified properties.
    /// </summary>
    /// <param name="containerName">The name of the container to create.</param>
    /// <param name="vectorPath">The path for the vector embedding.</param>
    /// <param name="partitionKeyPaths">The list of partition key paths.</param>
    /// <param name="includedPaths">The list of included paths for indexing.</param>
    /// <param name="dimensions">The dimensions for the vector embedding.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task CreateContainerAsync(
        string containerName,
        string vectorPath,
        List<string> partitionKeyPaths,
        List<string> includedPaths,
        int dimensions)
    {
        _logger.LogInformation($"Creating container '{containerName}' with partition key paths: {string.Join(", ", partitionKeyPaths)}");

        Database database = _cosmosClient.GetDatabase(_databaseId);

        Collection<Microsoft.Azure.Cosmos.Embedding> embeddings = new Collection<Microsoft.Azure.Cosmos.Embedding>()
            {
                new Microsoft.Azure.Cosmos.Embedding()
                {
                    Path = vectorPath,
                    DataType = VectorDataType.Float32,
                    DistanceFunction = DistanceFunction.Cosine,
                    Dimensions = dimensions
                }
            };

        ContainerProperties containerProperties = new ContainerProperties(
            id: containerName,
            partitionKeyPaths: partitionKeyPaths
        )
        {
            VectorEmbeddingPolicy = new VectorEmbeddingPolicy(embeddings),
            IndexingPolicy = new IndexingPolicy()
            {
                VectorIndexes = new Collection<VectorIndexPath>()
                    {
                        new VectorIndexPath()
                        {
                            Path = vectorPath,
                            Type = VectorIndexType.DiskANN,
                        }
                    }
            }
        };

        foreach (string path in includedPaths)
        {
            containerProperties.IndexingPolicy.IncludedPaths.Add(new IncludedPath { Path = path });
        }

        containerProperties.IndexingPolicy.ExcludedPaths.Add(new ExcludedPath { Path = "/\"_etag\"/?" });

        Container container = await database.CreateContainerIfNotExistsAsync(containerProperties);

        _logger.LogInformation($"Container created or exists: {container.Id}");
    }

    /// <summary>
    /// Lists all databases and their containers in the Cosmos DB account.
    /// </summary>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task ListDatabasesAndContainersAsync()
    {
        _logger.LogInformation("Fetching databases and containers...");

        using FeedIterator<DatabaseProperties> databaseIterator = _cosmosClient.GetDatabaseQueryIterator<DatabaseProperties>();
        while (databaseIterator.HasMoreResults)
        {
            FeedResponse<DatabaseProperties> databases = await databaseIterator.ReadNextAsync();

            foreach (var database in databases)
            {
                Console.WriteLine($"Database ID: {database.Id}");

                Database db = _cosmosClient.GetDatabase(database.Id);

                using FeedIterator<ContainerProperties> containerIterator = db.GetContainerQueryIterator<ContainerProperties>();
                while (containerIterator.HasMoreResults)
                {
                    FeedResponse<ContainerProperties> containers = await containerIterator.ReadNextAsync();
                    foreach (var container in containers)
                    {
                        Console.WriteLine($"\tContainer ID: {container.Id}");
                        Console.WriteLine($"\t  Partition Key Paths: {string.Join(", ", container.PartitionKeyPaths)}");
                        Console.WriteLine($"\t  Default Time to Live (TTL): {container.DefaultTimeToLive}");
                    }
                }
            }
        }

        _logger.LogInformation("Finished fetching database and container information.");
    }

    /// <summary>
    /// Handles the input file from a specified path.
    /// This method processes a PDF file using the RAG chat service.
    /// </summary>
    /// <param name="tenantID">The tenant ID.</param>
    /// <param name="userID">The user ID.</param>
    /// <returns>A Task representing the asynchronous operation, with a string result indicating the success of the operation.</returns>
    public async Task<string> HandleInputFileFromPath(
            string tenantID,
            string userID)
    {

        var filePath = "tsla-20231231.htm.html.pdf";

        // Generate a memoryKey based on tenantID, userID, and fileName
        var fileName = "tsla-20231231.htm.html.pdf";
        var memoryKey = $"{tenantID}-{userID}-{fileName}";
        Console.WriteLine($"HandleInputFileFromPath memoryKey: {memoryKey}");

        try
        {
            // Call ProcessPdfAsync directly
            Console.WriteLine($"Calling ProcessPdfAsync for {filePath}");
            await _ragChatService.ProcessPdfAsync(
                tenantID,
                userID,
                fileName,
                filePath,
                memoryKey,
                CancellationToken.None);

            Console.WriteLine("ProcessPdfAsync completed successfully");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in HandleInputFileFromPath: {ex}");
            throw;
        }
        finally
        {

            Console.WriteLine($"HandleInputFileFromPath completed");
        }

        return $"Successfully processed file: {fileName}";
    }

    /// <summary>
    /// Handles the Knowledge Base Completion Command asynchronously.
    /// This method calls the GetKnowledgeBaseCompletionAsync method from the chat service
    /// to retrieve a completion based on the provided parameters.
    /// </summary>
    /// <param name="tenantId">The tenant ID.</param>
    /// <param name="userId">The user ID.</param>
    /// <param name="categoryId">The category ID.</param>
    /// <param name="promptText">The prompt text to generate the completion.</param>
    /// <param name="similarityScore">The similarity score threshold for the completion.</param>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task HandleKnowledgeBaseCompletionCommandAsync(
        string tenantId,
        string userId,
        string categoryId,
        string promptText,
        double similarityScore)
    {
        Console.WriteLine("Calling GetKnowledgeBaseCompletionAsync...");
        try
        {
            var (completion, title) = await _chatService.GetKnowledgeBaseCompletionAsync(
                tenantId: tenantId,
                userId: userId,
                categoryId: categoryId,
                promptText: promptText,
                similarityScore: similarityScore);

            Console.WriteLine($"Completion Title: {title ?? "No Title"}");
            Console.WriteLine($"Completion Text:\n{completion}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error occurred while calling GetKnowledgeBaseCompletionAsync.");
            Console.WriteLine($"Error: {ex.Message}");
        }
    }


    /// <summary>
    /// Handles the Phi 3.5 MoE Instruct Command asynchronously using HTTP.
    /// This method sets up a conversational assistant using Azure AI services via HTTP requests.
    /// </summary>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task HandlePhi35MoEInstructCommandAsync1()
    {

        var handler = new HttpClientHandler()
        {
            ClientCertificateOptions = ClientCertificateOption.Manual,
            ServerCertificateCustomValidationCallback =
                    (httpRequestMessage, cert, cetChain, policyErrors) => { return true; }
        };
        using (var client = new HttpClient(handler))
        {
            // Request data goes here
            // The example below assumes JSON formatting which may be updated
            // depending on the format your endpoint expects.
            // More information can be found here:
            // https://docs.microsoft.com/azure/machine-learning/how-to-deploy-advanced-entry-script
            var requestBody = @"{
                  ""messages"": [
                    {
                      ""role"": ""user"",
                      ""content"": ""I am going to Paris, what should I see?""
                    },
                    {
                      ""role"": ""assistant"",
                      ""content"": ""Paris, the capital of France, is known for its stunning architecture, art museums, historical landmarks, and romantic atmosphere. Here are some of the top attractions to see in Paris:\n\n1. The Eiffel Tower: The iconic Eiffel Tower is one of the most recognizable landmarks in the world and offers breathtaking views of the city.\n2. The Louvre Museum: The Louvre is one of the world's largest and most famous museums, housing an impressive collection of art and artifacts, including the Mona Lisa.\n3. Notre-Dame Cathedral: This beautiful cathedral is one of the most famous landmarks in Paris and is known for its Gothic architecture and stunning stained glass windows.\n\nThese are just a few of the many attractions that Paris has to offer. With so much to see and do, it's no wonder that Paris is one of the most popular tourist destinations in the world.""
                    },
                    {
                      ""role"": ""user"",
                      ""content"": ""What is so great about #1?""
                    }
                  ],
                  ""max_tokens"": 2048,
                  ""temperature"": 0.8,
                  ""top_p"": 0.1,
                  ""presence_penalty"": 0,
                  ""frequency_penalty"": 0
                }";

            // Replace this with the primary/secondary key, AMLToken, or Microsoft Entra ID token for the endpoint
            string apiKey = GetEnvironmentVariable("PHI_KEY");

            if (string.IsNullOrEmpty(apiKey))
            {
                throw new Exception("A key should be provided to invoke the endpoint");
            }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            client.BaseAddress = new Uri(GetEnvironmentVariable("PHI_ENDPOINT"));

            var content = new StringContent(requestBody);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

            // WARNING: The 'await' statement below can result in a deadlock
            // if you are calling this code from the UI thread of an ASP.Net application.
            // One way to address this would be to call ConfigureAwait(false)
            // so that the execution does not attempt to resume on the original context.
            // For instance, replace code such as:
            //      result = await DoSomeTask()
            // with the following:
            //      result = await DoSomeTask().ConfigureAwait(false)
            HttpResponseMessage response = await client.PostAsync("", content);

            if (response.IsSuccessStatusCode)
            {
                string result = await response.Content.ReadAsStringAsync();
                Console.WriteLine("Result: {0}", result);
            }
            else
            {
                Console.WriteLine(string.Format("The request failed with status code: {0}", response.StatusCode));

                // Print the headers - they include the requert ID and the timestamp,
                // which are useful for debugging the failure
                Console.WriteLine(response.Headers.ToString());

                string responseContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine(responseContent);
            }
        }

    }

    /// <summary>
    /// Handles the Phi 3.5 MoE Instruct Command asynchronously using HTTP.
    /// This method sets up a conversational assistant using Azure AI services via HTTP requests.
    /// </summary>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task HandlePhi35MoEInstructCommandHttpAsync01()
    {
        // Configuration
        string deploymentName = "phi-3-5-moe-instruct"; // Deployment name from Azure
        string endpoint = GetEnvironmentVariable("PHI_ENDPOINT");
        string apiKey = GetEnvironmentVariable("PHI_KEY");

        // Set up HttpClient with Authorization header
        using var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {apiKey}");

        // Prepare the request payload
        var requestBody = new
        {
            messages = new[]
            {
            new { role = "user", content = "Hello" } // Example user message
        }
        };

        // Serialize the request body to JSON
        string jsonPayload = System.Text.Json.JsonSerializer.Serialize(requestBody);

        // Create the HTTP request
        var request = new HttpRequestMessage
        {
            Method = HttpMethod.Post,
            RequestUri = new Uri(endpoint),
            Content = new StringContent(jsonPayload, System.Text.Encoding.UTF8, "application/json")
        };

        try
        {
            // Send the request
            var response = await httpClient.SendAsync(request);

            // Ensure the response is successful
            response.EnsureSuccessStatusCode();

            // Read and process the response
            string responseBody = await response.Content.ReadAsStringAsync();
            Console.WriteLine("Response:");
            Console.WriteLine(responseBody);
        }
        catch (HttpRequestException ex)
        {
            // Handle HTTP errors
            Console.WriteLine($"Request error: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles the Phi 3.5 MoE Instruct Command asynchronously.
    /// This method sets up a conversational assistant using Azure AI services.
    /// </summary>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task HandlePhi35MoEInstructCommandAsync2()
    {
        string endpoint = GetEnvironmentVariable("PHI_ENDPOINT");
        string apiKey = GetEnvironmentVariable("PHI_KEY");
        string modelId = "phi-3-5-moe-instruct";

        // Kernel builder
        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddAzureAIInferenceChatCompletion(
            endpoint: new Uri(endpoint),
            apiKey: apiKey,
            modelId: modelId//,
                            //httpClient: httpClient // Provide the custom HttpClient
        );

        var kernel = kernelBuilder.Build();

        // Create chat service
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var history = new ChatHistory();

        // Chat loop
        while (true)
        {
            Console.Write("Q: ");
            var userQ = Console.ReadLine();
            if (string.IsNullOrEmpty(userQ))
            {
                break;
            }

            history.AddUserMessage(userQ);

            Console.Write("Phi3: ");
            string response = string.Empty;

            var result = chat.GetStreamingChatMessageContentsAsync(history);

            await foreach (var message in result)
            {
                Console.Write(message.Content);
                response += message.Content;
            }

            history.AddAssistantMessage(response);
            Console.WriteLine();
        }
    }


    /// <summary>
    /// Handles the Phi 3.5 MoE Instruct Command asynchronously.
    /// This method sets up a conversational assistant using Azure AI services.
    /// using semantic kernel
    /// </summary>
    /// <returns>A Task representing the asynchronous operation.</returns>
    public async Task HandlePhi35MoEInstructCommandAsyncs()
    {
        string endpoint = GetEnvironmentVariable("PHI_ENDPOINT");
        string apiKey = GetEnvironmentVariable("PHI_KEY");
        string modelId = "phi-3-5-moe-instruct";

        // Kernel builder setup
        var kernelBuilder = Kernel.CreateBuilder();
        kernelBuilder.AddAzureAIInferenceChatCompletion(
            endpoint: new Uri(endpoint),
            apiKey: apiKey,
            modelId: modelId
        );

        var kernel = kernelBuilder.Build();

        // Define the prompt template for general chat
        var FunctionDefinition = @"""
You are a helpful, concise, and conversational assistant. Answer user questions accurately and engagingly while keeping responses brief but informative. Follow these instructions:

- Provide helpful responses to general inquiries or open-ended questions.
- Avoid including irrelevant information or making assumptions.
- Respond directly to the user's query.

{{$input}}

    """;
        var paragraphWritingFunction = kernel.CreateFunctionFromPrompt(FunctionDefinition);

        // Chat settings
        double temperature = 0.7;
        double topP = 0.9;
        int maxTokens = 1000;

        var executionSettings = new AzureOpenAIPromptExecutionSettings
        {
            Temperature = temperature,
            TopP = topP,
            MaxTokens = maxTokens,
        };

        Console.WriteLine("General Chat Assistant. Type 'exit' to quit.");
        while (true)
        {
            Console.Write("Q: ");
            var userInput = Console.ReadLine();
            if (string.IsNullOrEmpty(userInput) || userInput.ToLower() == "exit")
            {
                break;
            }


            // Set arguments for execution
            var arguments = new KernelArguments(executionSettings)
            {
                ["input"] = userInput
            };

            try
            {
                Console.Write("Phi3: ");

                var result = await paragraphWritingFunction.InvokeAsync(kernel, arguments);

                var resultValue = result.GetValue<string>();
                Console.WriteLine(resultValue);

                Console.WriteLine();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

        }
    }

    static string GetEnvironmentVariable(string variableName)
    {
        return Environment.GetEnvironmentVariable(variableName)
            ?? throw new ArgumentNullException(variableName, $"{variableName} is not set in environment variables.");
    }
}