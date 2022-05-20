# This script exists because putting the following line directly into a script block in gitlab-ci.yml doesn't work.
.\runBuild.ps1 -Target pack -PackVersion $env:CI_COMMIT_TAG
