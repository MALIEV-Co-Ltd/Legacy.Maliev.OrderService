#nullable disable
// Original 5fac CLR body; namespace relocated for isolated read-contract witness.
// <copyright file="PaginatedListWebApi.cs" company="Maliev Company Limited">
// Copyright (c) Maliev Company Limited. All rights reserved.
// </copyright>

namespace Maliev.OrderService.Tests.SourceOrderReads
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.EntityFrameworkCore;

    /// <summary>
    ///   <see cref="PaginatedListWebApi{T}"/>
    /// </summary>
    /// <typeparam name="T">type of source</typeparam>
    public class PaginatedListWebApi<T>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="PaginatedListWebApi{T}" /> class.
        /// </summary>
        public PaginatedListWebApi()
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="PaginatedListWebApi{T}" /> class.
        /// </summary>
        /// <param name="items">list of source items</param>
        /// <param name="count">item count</param>
        /// <param name="pageIndex">page index</param>
        /// <param name="pageSize">page size</param>
        public PaginatedListWebApi(List<T> items, int count, int pageIndex, int pageSize)
        {
            this.PageIndex = pageIndex;
            this.TotalPages = (int)Math.Ceiling(count / (double)pageSize);
            this.TotalRecords = count;

            this.Items.AddRange(items);
        }

        /// <summary>
        /// Gets a value indicating whether list has next page
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance has next page; otherwise, <c>false</c>.
        /// </value>
        public bool HasNextPage => this.PageIndex < this.TotalPages;

        /// <summary>
        /// Gets a value indicating whether list has previous page
        /// </summary>
        /// <value>
        ///   <c>true</c> if this instance has previous page; otherwise, <c>false</c>.
        /// </value>
        public bool HasPreviousPage => this.PageIndex > 1;

        /// <summary>
        /// Gets or sets the items.
        /// </summary>
        /// <value>
        /// The items.
        /// </value>
        public List<T> Items { get; set; } = new List<T>();

        /// <summary>
        /// Gets or sets page index
        /// </summary>
        /// <value>
        /// The index of the page.
        /// </value>
        public int PageIndex { get; set; }

        /// <summary>
        /// Gets or sets total pages
        /// </summary>
        /// <value>
        /// The total pages.
        /// </value>
        public int TotalPages { get; set; }

        /// <summary>
        /// Gets or sets the total records.
        /// </summary>
        /// <value>
        /// The total records.
        /// </value>
        public int TotalRecords { get; set; }

        /// <summary>
        /// Create paginated list
        /// </summary>
        /// <param name="source"><see cref="IQueryable{T}" /> of source item</param>
        /// <param name="pageIndex">page index</param>
        /// <param name="pageSize">page size</param>
        /// <returns>
        ///   <see cref="PaginatedListWebApi{T}" />
        /// </returns>
        public static async Task<PaginatedListWebApi<T>> CreateAsync(IQueryable<T> source, int pageIndex, int pageSize)
        {
            pageIndex = Math.Max(1, pageIndex);

            var count = await source.CountAsync();
            if (count == 0)
            {
                return null;
            }

            var items = await source.Skip((pageIndex - 1) * pageSize).Take(pageSize).ToListAsync();
            return new PaginatedListWebApi<T>(items, count, pageIndex, pageSize);
        }
    }
}
