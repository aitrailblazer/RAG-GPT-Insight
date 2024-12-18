﻿using Cosmos.Copilot.Models;
using Microsoft.ML.Tokenizers;
using Microsoft.Extensions.Logging; // Added for logging
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json;
using PartitionKey = Microsoft.Azure.Cosmos.PartitionKey;
using Microsoft.Azure.Cosmos;
using System.Diagnostics;
using System.Text;


public class ChatService
{
    private readonly CosmosDbService _cosmosDbService;
    private readonly SemanticKernelService _semanticKernelService;
    private readonly int _maxConversationTokens;
    private readonly double _cacheSimilarityScore;

    private readonly int _knowledgeBaseMaxResults;

    private readonly ILogger<ChatService> _logger; // Logger instance

    public ChatService(
        CosmosDbService cosmosDbService,
        SemanticKernelService semanticKernelService,
        string maxConversationTokens,
        string cacheSimilarityScore,
        ILogger<ChatService> logger) // Injected logger
    {
        _cosmosDbService = cosmosDbService ?? throw new ArgumentNullException(nameof(cosmosDbService));
        _semanticKernelService = semanticKernelService ?? throw new ArgumentNullException(nameof(semanticKernelService));

        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        //if (!Int32.TryParse(maxConversationTokens, out _maxConversationTokens))
        //{
        //    _logger.LogWarning("Invalid maxConversationTokens value '{Value}'. Defaulting to 100.", maxConversationTokens);
        //    _maxConversationTokens = 100;
        //}
        _maxConversationTokens = 4096;

        //if (!Double.TryParse(cacheSimilarityScore, out _cacheSimilarityScore))
        //{
        //    _logger.LogWarning("Invalid cacheSimilarityScore value '{Value}'. Defaulting to 0.99.", cacheSimilarityScore);
        //    _cacheSimilarityScore = 0.90;
        //}
        _cacheSimilarityScore = 0.90;
        //if (!Int32.TryParse(productMaxResults, out _productMaxResults))
        //{
        //    _logger.LogWarning("Invalid productMaxResults value '{Value}'. Defaulting to 10.", productMaxResults);
        //    _productMaxResults = 10;
        //}
        _knowledgeBaseMaxResults = 10;

        //_logger.LogInformation("ChatService initialized with MaxConversationTokens={MaxConversationTokens}, CacheSimilarityScore={CacheSimilarityScore}, ProductMaxResults={ProductMaxResults}",
        //_maxConversationTokens, _cacheSimilarityScore, _productMaxResults);
    }

    public async Task<(string completion, string? title)> GetKnowledgeBaseCompletionAsync(
        string tenantId,
        string userId,
        string categoryId,
        string promptText,
        double similarityScore)
    {
        try
        {
            // Validate inputs
            ArgumentNullException.ThrowIfNull(tenantId, nameof(tenantId));
            ArgumentNullException.ThrowIfNull(userId, nameof(userId));

            // Generate embeddings for the prompt
            float[] promptVectors = await _semanticKernelService.GetEmbeddingsAsync(promptText);
            _logger.LogInformation("Embeddings generated for the prompt.");

            // Generate keywords from promptText
            string[] searchTerms = GenerateKeywords(promptText);

            // Search for knowledge base items
            List<KnowledgeBaseItem> items = await _cosmosDbService.SearchKnowledgeBaseAsync(
                vectors: promptVectors,
                tenantId: tenantId,
                userId: userId,
                categoryId: categoryId,
                similarityScore: similarityScore,
                searchTerms: searchTerms
            );

            if (items == null || items.Count == 0)
            {
                _logger.LogInformation("No similar knowledge base items found.");
                return (string.Empty, null);
            }

            // Generate completions for all items and combine results
            var completions = new List<string>();

            foreach (var item in items)
            {
                _logger.LogInformation("Processing item: {Title}", item.Title);

                var (generatedCompletion, _) = await _semanticKernelService.GetASAPQuick<KnowledgeBaseItem>(
                    input: promptText,
                    contextData: item
                );
                // Log the intermediate generated completion
                _logger.LogInformation("Intermediate Completion for {Title}: {Completion}", item.Title, generatedCompletion);

                completions.Add(generatedCompletion);
            }

            // Combine all completions into a single result
            string combinedCompletion = string.Join("\n\n", completions);

            _logger.LogInformation("Completion generated for all knowledge base items.");

            // Return the combined result and title of the first item
            return (combinedCompletion, items.First().Title);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating knowledge base completion.");
            throw;
        }
    }
    /*
        public async Task<(string completion, string? title)> GetKnowledgeBaseCompletionAsync01(
            string tenantId,
            string userId,
            string categoryId,
            string promptText,
            double similarityScore)
        {
            //_logger.LogInformation("Generating knowledge base completion for TenantId={TenantId}, UserId={UserId}, CategoryId={CategoryId}.", tenantId, userId, categoryId ?? "None");

            try
            {
                // Validate inputs
                ArgumentNullException.ThrowIfNull(tenantId, nameof(tenantId));
                ArgumentNullException.ThrowIfNull(userId, nameof(userId));

                // Generate embeddings for the prompt
                float[] promptVectors = await _semanticKernelService.GetEmbeddingsAsync(promptText);
                _logger.LogInformation("GetKnowledgeBaseCompletionAsync Embeddings generated for the prompt.");

                // Generate keywords from promptText
                string[] searchTerms = GenerateKeywords(promptText);

                // Search for the closest knowledge base item
                KnowledgeBaseItem? closestItem = await _cosmosDbService.SearchKnowledgeBaseAsync(
                    vectors: promptVectors,
                    tenantId: tenantId,
                    userId: userId,
                    categoryId: categoryId,
                    similarityScore: similarityScore,
                    searchTerms: searchTerms);

                if (closestItem == null)
                {
                    _logger.LogInformation("GetKnowledgeBaseCompletionAsync No similar knowledge base items found.");
                    return (string.Empty, null);
                }

                _logger.LogDebug("GetKnowledgeBaseCompletionAsync Found closest item: {Title}.", closestItem.Title);

                // Generate completion using the found item
                var contextWindow = new List<KnowledgeBaseItem> { closestItem }; // Create context window with the closest item
                (string generatedCompletion, int tokens) = await _semanticKernelService.GetASAPQuick<KnowledgeBaseItem>(
                    input: promptText,
                    contextData: closestItem
                    );

                _logger.LogInformation("GetKnowledgeBaseCompletionAsync Completion generated for knowledge base.");

                return (generatedCompletion, closestItem.Title);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetKnowledgeBaseCompletionAsync Error generating knowledge base completion for TenantId={TenantId}, UserId={UserId}, CategoryId={CategoryId}.", tenantId, userId, categoryId ?? "None");
                throw;
            }
        }
        */
    private string[] GenerateKeywords(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText))
            throw new ArgumentException("Prompt text cannot be null or empty.", nameof(promptText));

        // Tokenize the text: Split into words by common delimiters
        var keywords = promptText
            .Split(new[] { ' ', ',', '.', ';', ':', '\n', '\t', '!', '?' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim().ToLowerInvariant()) // Normalize to lowercase
            .Distinct() // Remove duplicates
            .Where(word => word.Length > 2) // Exclude very short words
            .ToArray();

        return keywords;
    }


}

