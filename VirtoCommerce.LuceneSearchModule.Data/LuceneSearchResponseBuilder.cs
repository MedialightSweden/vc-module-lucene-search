﻿using System;
using System.Collections.Generic;
using System.Linq;
using Lucene.Net.Documents;
using Lucene.Net.Search;
using VirtoCommerce.Domain.Search;
using VirtoCommerce.Platform.Core.Common;

namespace VirtoCommerce.LuceneSearchModule.Data
{
    public static class LuceneSearchResponseBuilder
    {
        public static SearchResponse ToSearchResponse(this TopDocs response, SearchRequest request, IndexSearcher searcher, string documentType, ICollection<string> availableFields)
        {
            var result = new SearchResponse
            {
                TotalCount = response.TotalHits,
                Documents = GetDocuments(response, request, searcher),
                Aggregations = GetAggregations(request, searcher, availableFields)
            };

            return result;
        }


        private static IList<SearchDocument> GetDocuments(TopDocs response, SearchRequest request, IndexSearcher searcher)
        {
            var result = new List<SearchDocument>();

            var maxIndex = Math.Min(response.TotalHits, request.Skip + request.Take);

            for (var i = request.Skip; i < maxIndex; i++)
            {
                var providerDocument = searcher.Doc(response.ScoreDocs[i].Doc);
                var document = ToSearchDocument(providerDocument);
                result.Add(document);
            }

            return result;
        }

        private static SearchDocument ToSearchDocument(Document providerDocument)
        {
            var result = new SearchDocument();

            var documentFields = providerDocument.GetFields();

            foreach (var field in documentFields)
            {
                if (field.Name.EqualsInvariant(LuceneSearchHelper.KeyFieldName))
                {
                    result.Id = field.StringValue;
                }
                else
                {
                    if (result.ContainsKey(field.Name)) // convert to array
                    {
                        var newValues = new List<object>();

                        var currentValue = result[field.Name];
                        var currentValues = currentValue as object[];

                        if (currentValues != null)
                        {
                            newValues.AddRange(currentValues);
                        }
                        else
                        {
                            newValues.Add(currentValue);
                        }

                        newValues.Add(field.StringValue);
                        result[field.Name] = newValues.ToArray();
                    }
                    else
                    {
                        result.Add(field.Name, field.StringValue);
                    }
                }
            }

            return result;
        }

        private static IList<AggregationResponse> GetAggregations(SearchRequest request, IndexSearcher searcher, ICollection<string> availableFields)
        {
            IList<AggregationResponse> result = null;

            if (request.Aggregations != null)
            {
                result = request.Aggregations
                    .Select(a => GetAggregation(a, searcher, availableFields))
                    .Where(a => a?.Values != null && a.Values.Any())
                    .ToList();
            }

            return result;
        }

        private static AggregationResponse GetAggregation(AggregationRequest aggregation, IndexSearcher searcher, ICollection<string> availableFields)
        {
            AggregationResponse result = null;

            var termAggregationRequest = aggregation as TermAggregationRequest;
            var rangeAggregationRequest = aggregation as RangeAggregationRequest;

            if (termAggregationRequest != null)
            {
                result = CreateTermAggregationResponse(termAggregationRequest, searcher, availableFields);
            }
            else if (rangeAggregationRequest != null)
            {
                result = CreateRangeAggregationResponse(rangeAggregationRequest, searcher, availableFields);
            }

            return result;
        }

        private static AggregationResponse CreateTermAggregationResponse(TermAggregationRequest termAggregationRequest, IndexSearcher searcher, ICollection<string> availableFields)
        {
            AggregationResponse result = null;

            if (termAggregationRequest != null)
            {
                var fieldName = GetFacetFieldName(termAggregationRequest.FieldName, availableFields);
                var values = termAggregationRequest.Values ?? searcher.IndexReader.GetAllFieldValues(fieldName);
                var valueFilters = values?.ToDictionary(v => v, v => LuceneSearchFilterBuilder.CreateTermsFilter(fieldName, v));

                result = GetAggregation(termAggregationRequest, valueFilters, searcher, true, termAggregationRequest.Size, availableFields);
            }

            return result;
        }

        private static string GetFacetFieldName(string originalName, ICollection<string> availableFields)
        {
            var result = LuceneSearchHelper.GetFacetableFieldName(originalName);

            if (!availableFields.Contains(result))
            {
                result = LuceneSearchHelper.ToLuceneFieldName(originalName);
            }

            return result;
        }

        private static AggregationResponse CreateRangeAggregationResponse(RangeAggregationRequest rangeAggregationRequest, IndexSearcher searcher, ICollection<string> availableFields)
        {
            AggregationResponse result = null;

            if (rangeAggregationRequest != null)
            {
                var fieldName = LuceneSearchHelper.ToLuceneFieldName(rangeAggregationRequest.FieldName);
                var valueFilters = rangeAggregationRequest.Values?.ToDictionary(v => v.Id, v => LuceneSearchFilterBuilder.CreateRangeFilterForValue(fieldName, v.Lower, v.Upper, v.IncludeLower, v.IncludeUpper));

                result = GetAggregation(rangeAggregationRequest, valueFilters, searcher, false, null, availableFields);
            }

            return result;
        }

        private static AggregationResponse GetAggregation(AggregationRequest aggregationRequest, IDictionary<string, Filter> valueFilters, IndexSearcher searcher, bool sortValues, int? maxValuesCount, ICollection<string> availableFields)
        {
            AggregationResponse result = null;

            if (aggregationRequest != null)
            {
                if (valueFilters != null)
                {
                    var commonFilter = LuceneSearchFilterBuilder.GetFilterRecursive(aggregationRequest.Filter, availableFields);
                    var values = valueFilters.Select(kvp => GetAggregationValue(kvp.Key, kvp.Value, commonFilter, searcher))
                        .Where(v => v != null);

                    if (sortValues)
                    {
                        values = values
                            .OrderByDescending(v => v.Count)
                            .ThenBy(v => v.Id);
                    }

                    if (maxValuesCount > 0)
                    {
                        values = values.Take(maxValuesCount.Value);
                    }

                    result = new AggregationResponse
                    {
                        Id = (aggregationRequest.Id ?? aggregationRequest.FieldName).ToLowerInvariant(),
                        Values = values.ToArray(),
                    };
                }
            }

            return result;
        }

        private static AggregationResponseValue GetAggregationValue(string valueId, Filter valueFilter, Filter commonFilter, IndexSearcher searcher)
        {
            AggregationResponseValue result = null;

            var filter = LuceneSearchFilterBuilder.JoinNonEmptyFilters(new[] { commonFilter, valueFilter }, Occur.MUST);
            if (filter != null)
            {
                var count = CalculateFacetCount(searcher, filter);
                if (count > 0)
                {
                    result = new AggregationResponseValue { Id = valueId, Count = count };
                }
            }

            return result;
        }

        private static long CalculateFacetCount(IndexSearcher searcher, Filter filter)
        {
            var response = searcher.Search(new MatchAllDocsQuery(), filter, 1);
            return response.TotalHits;
        }
    }
}