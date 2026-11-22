# Which earlier releases a new build hides, and which one stays as the bridge (the newest release with a full zip, for launchers from before the parts
# layout, while the new release has no full zip). Pure: no gh, no files; release.ps1 dot-sources it and the tests call it with fake rows.
#   $Rows         every published (not draft) release: objects with Tag, At, Zip (a full playtest zip), Parts (a parts manifest)
#   $HasLocalParts   the local parts manifest of this build exists (normal ship only)
#   $ShipUploadsZip  this ship uploads a full zip (normal ship only)
#   $ArchiveOnly  the release was published by hand from a draft: decided ONLY from the published rows, never from local files
# Returns @{ Hide = tags to hide; Bridge = the kept tag or $null; Warn = text or $null }. On -ArchiveOnly an unreadable list, or a $Tag that is not among the
# published rows, hides nothing and warns.
function Get-BridgeDecision {
    param($Rows, [string]$Tag, [string]$PartsTag, [bool]$HasLocalParts, [bool]$ShipUploadsZip, [bool]$ArchiveOnly)
    $all = @($Rows | Where-Object { $_ })
    $others = @($all | Where-Object { $_.Tag -and $_.Tag -cne $Tag -and $_.Tag -cne $PartsTag })
    if ($ArchiveOnly) {
        if ($all.Count -eq 0) { return @{ Hide = @(); Bridge = $null; Warn = "No published releases could be read from GitHub, so nothing was hidden. Check the releases page, then hide the earlier builds by hand (keep the newest release with a full zip)." } }
        $self = @($all | Where-Object { $_.Tag -ceq $Tag })[0]
        if (-not $self) { return @{ Hide = @(); Bridge = $null; Warn = "$Tag is not among the published releases GitHub returned, so nothing was hidden. Check that it is published (not a draft) and that the tag is right, then run again." } }
        $newHasZip = [bool]$self.Zip
    }
    else { $newHasZip = $ShipUploadsZip -or -not $HasLocalParts -or [bool]@($all | Where-Object { $_.Tag -ceq $Tag -and $_.Zip }) }
    $bridge = $null
    if (-not $newHasZip) {
        $bridge = @($others | Where-Object { $_.Zip } | Sort-Object At -Descending)[0]
        if ($bridge) { $others = @($others | Where-Object { $_.Tag -cne $bridge.Tag }) }
    }
    return @{ Hide = @($others | ForEach-Object { $_.Tag }); Bridge = $(if ($bridge) { $bridge.Tag } else { $null }); Warn = $null }
}
