using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.SeriesTrackMemory.Storage;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.SeriesTrackMemory.Api;

/// <summary>
/// One learned preference, as shown in the configuration page.
/// </summary>
/// <param name="UserId">User id.</param>
/// <param name="UserName">User name.</param>
/// <param name="SeriesId">Series id.</param>
/// <param name="SeriesName">Series name.</param>
/// <param name="Audio">Audio label.</param>
/// <param name="Subtitle">Subtitle label.</param>
/// <param name="UpdatedUtc">When it was learned.</param>
public sealed record PreferenceDto(
    Guid UserId,
    string UserName,
    Guid SeriesId,
    string SeriesName,
    string Audio,
    string Subtitle,
    DateTime UpdatedUtc);

/// <summary>
/// Admin API to list and forget learned preferences.
/// </summary>
[ApiController]
[Route("SeriesTrackMemory")]
[Authorize(Policy = Policies.RequiresElevation)]
public class SeriesTrackMemoryController : ControllerBase
{
    private readonly PreferenceStore _store;
    private readonly SeriesTrackEngine _engine;
    private readonly IUserManager _userManager;

    /// <summary>
    /// Initializes a new instance of the <see cref="SeriesTrackMemoryController"/> class.
    /// </summary>
    /// <param name="store">Preference store.</param>
    /// <param name="engine">Engine.</param>
    /// <param name="userManager">User manager.</param>
    public SeriesTrackMemoryController(PreferenceStore store, SeriesTrackEngine engine, IUserManager userManager)
    {
        _store = store;
        _engine = engine;
        _userManager = userManager;
    }

    /// <summary>
    /// Lists learned preferences.
    /// </summary>
    /// <returns>The preferences.</returns>
    [HttpGet("Preferences")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult<IEnumerable<PreferenceDto>> GetPreferences()
    {
        return Ok(_store.GetAll().Select(p => new PreferenceDto(
            p.UserId,
            _userManager.GetUserById(p.UserId)?.Username ?? p.UserId.ToString("N"),
            p.SeriesId,
            p.SeriesName ?? p.SeriesId.ToString("N"),
            p.Audio?.ToString() ?? "default",
            SeriesTrackEngine.DescribeSubtitle(p),
            p.UpdatedUtc)));
    }

    /// <summary>
    /// Forgets a learned preference and clears the selections it wrote.
    /// </summary>
    /// <param name="userId">User id.</param>
    /// <param name="seriesId">Series id.</param>
    /// <returns>No content, or not found.</returns>
    [HttpDelete("Preferences/{userId}/{seriesId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Forget([FromRoute] Guid userId, [FromRoute] Guid seriesId)
    {
        return _engine.Forget(userId, seriesId) ? NoContent() : NotFound();
    }
}
