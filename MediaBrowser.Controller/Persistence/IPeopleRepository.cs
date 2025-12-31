#nullable disable

#pragma warning disable CS1591

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;

namespace MediaBrowser.Controller.Persistence;

public interface IPeopleRepository
{
    /// <summary>
    /// Gets the people.
    /// </summary>
    /// <param name="filter">The query.</param>
    /// <param name="token">The <see cref="CancellationToken"/>.</param>
    /// <returns>The list of people matching the filter.</returns>
    Task<IReadOnlyList<PersonInfo>> GetPeopleAsync(InternalPeopleQuery filter, CancellationToken token = default);

    /// <summary>
    /// Updates the people.
    /// </summary>
    /// <param name="itemId">The item identifier.</param>
    /// <param name="people">The people.</param>
    /// <param name="token">The <see cref="CancellationToken"/>.</param>
    /// <returns>The async <see cref="Task"/>.</returns>
    Task UpdatePeopleAsync(Guid itemId, IReadOnlyList<PersonInfo> people, CancellationToken token = default);

    /// <summary>
    /// Gets the people names.
    /// </summary>
    /// <param name="filter">The query.</param>
    /// <param name="token">The <see cref="CancellationToken"/>.</param>
    /// <returns>The list of people names matching the filter.</returns>
    Task<IReadOnlyList<string>> GetPeopleNamesAsync(InternalPeopleQuery filter, CancellationToken token = default);
}
