@echo off
rem Ships the next playtest build to Yeezima/sm-reimpl-releases in one command (owner only): preflight (clean main = origin/main, tests,
rem release\NEXT.md), build, content guard, smoke test, publish (a pre-release; -Draft keeps a draft), archive release\NEXT.md.
rem   ship.cmd -DryRun        the checks and every command, nothing built or published
rem   ship.cmd -Playtest      and restart the local playtest server (127.0.0.1:27025) with 7 bots on the shipped build
rem Publishing hides the earlier builds (turns them back into drafts); -KeepPrevious leaves them visible.
rem Other options: -SkipTests -Draft -Supersede -KeepPrevious -NotPrerelease -NoPush -AllowEmptyNotes. See docs\releasing.md.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\release\release.ps1" -Ship %*
