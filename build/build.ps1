$projName="SqlFu"
$buildDir=$psake.build_script_dir
$rootDir=Split-Path $buildDir
$projectDir=$rootDir+"\src\"+$projName
$projectFile=$projectDir+"\SqlFu.csproj"
$buildOutputPath =$projectDir+"\bin\Release"
$tempDir=join-path $rootDir "temp"
$nuspec=join-path $tempDir "project.nuspec"
$nugetOut=join-path $rootDir "nuget"
$nuget=join-path $rootDir "libs\nuget\nuget.exe"

Framework "4.0"
# Framework "4.0x64"


task default -depends clean,build,nuget

task clean{
   Write-Host "Cleaning..."
   msbuild $projectFile /t:Clean /v:quiet
   rd $tempDir -recurse
   mkdir $tempDir
}

task build {
    msbuild $projectFile /t:Build /p:Configuration=Release /v:quiet
}


task nuget{
   mkdir "$tempDir\lib\Net40"
    xcopy "$buildOutputPath\$projName*.*"  "$tempDir\lib\Net40"
   xcopy "$buildDir\project.nuspec" (split-path $nuspec)
      $specFile=[xml](Get-Content $nuspec)
	  	   $specFile.package.metadata.version=GetVersion	  
			$specFile.package.metadata.dependencies.dependency.version=CavemanVersion
	  
	      $specFile.Save($nuspec)
   "Updated version"
   "Building package..."
   & $nuget pack $nuspec -o $nugetOut
   
}

function GetVersion
{
 "1.0.3"
}

function CavemanVersion
{
 "1.4.0"
}