
You are an expert F#, .NET 10, macOS, Linux, and Windows systems programmer.

Your task is to create a production-quality F# script (.fsx) called:

    DiskCleanupAdvisor.fsx

This is NOT a toy project.

The code should be written as though it will eventually become an open-source project.

The design should emphasize:

• Safety
• Extensibility
• Functional Programming
• Performance
• Testability
• Clean Architecture
• Excellent diagnostics

========================================================
GENERAL REQUIREMENTS
====================

Target

• F# 9
• .NET 10
• Single .fsx script
• No third-party libraries unless absolutely necessary.
• Cross-platform.
• Runs on:

    macOS
    Linux
    Windows

using RuntimeInformation.

Use immutable data whenever practical.

Avoid mutable state except where performance requires.

Use Result<'T,'Error>

Use Async where beneficial.

Use parallel directory scanning where appropriate.

No UI.

Console application.

========================================================
ARCHITECTURE
============

Split the script into logical modules.

Examples

Types

Platform

Rules

Scanner

DirectoryWalker

Hashing

RiskEngine

FileClassifier

Report

Markdown

Html

Json

ConsoleOutput

Cleaner

Configuration

CLI

Logging

Utilities

Diagnostics

========================================================
RULE ENGINE
===========

Create a rule engine.

Each rule should contain

Rule Name

Platform

Path Pattern

File Pattern

Risk Level

Recoverability

Description

Reason

Safe To Delete

Delete Strategy

Estimated Benefit

Examples

The engine should support adding new rules without modifying the scanner.

========================================================
RISK LEVELS
===========

Safe

ProbablySafe

Review

Dangerous

NeverDelete

Each should include

Color

Console Icon

Description

========================================================
SCAN TYPES
==========

Scan

Caches

Logs

Temporary Files

Crash Reports

Developer Artifacts

Old Archives

Large Files

Duplicate Files

Empty Directories

Unused Applications

Downloads

Package Manager Caches

Browser Caches

Virtual Machines

Containers

Old Installers

ISO Files

Disk Images

Zip Files

NuGet

npm

pnpm

Yarn

Pip

Gradle

Maven

Homebrew

Xcode

Visual Studio

Rider

Docker

Podman

Terraform

Kubernetes

========================================================
macOS SPECIFIC RULES
====================

Safely detect

~/Library/Caches

~/Library/Logs

~/Library/Application Support/CrashReporter

~/Library/Developer/Xcode/DerivedData

~/Library/Developer/Xcode/Archives

~/Library/Developer/CoreSimulator

~/.Trash

Browser caches

NuGet cache

Homebrew cache

npm cache

pnpm cache

Docker cache

Podman cache

Terraform plugin cache

Python cache

Node cache

Within

~/Library/Containers

only classify as Safe

Data/Library/Caches

Data/tmp

Everything else inside Containers

must be classified

Review

Never automatically delete anything inside

Application Support

Documents

Desktop

Pictures

Movies

Music

Downloads

System

Library

except explicitly safe cache locations.

========================================================
WINDOWS RULES
=============

Detect

Temp

Prefetch

Windows Update cache

NuGet

Visual Studio

Rider

Recycle Bin

========================================================
LINUX RULES
===========

Detect

/tmp

/var/tmp

package caches

journal logs

snap cache

flatpak cache

========================================================
SCANNING
========

Compute

File Count

Directory Size

Last Access

Last Modified

Largest Files

Largest Directories

Age

Hash duplicates

Permissions

Owner

========================================================
HASHING
=======

Support

SHA256

MD5

Fast hash

Only hash files larger than configurable size.

========================================================
REPORTS
=======

Generate

Console Report

Markdown

HTML

JSON

Include

Pie charts using SVG

Treemap

Largest folders

Largest files

Recoverable Space

Risk Summary

Recommendations

========================================================
HTML REPORT
===========

Responsive

Dark Mode

Collapsible sections

Search

Sorting

Filtering

Progress bars

Risk colors

========================================================
DRY RUN
=======

Default mode

Dry Run

Nothing is deleted.

========================================================
DELETE MODE
===========

Require

--delete

Then

Ask for confirmation

Then

Delete only

RiskLevel == Safe

unless

--force

========================================================
COMMAND LINE
============

Support

--scan

--delete

--json

--markdown

--html

--duplicates

--large-files

--age

--min-size

--threads

--verbose

--dry-run

--force

--config

========================================================
CONFIGURATION
=============

Support JSON configuration.

Include

Ignore Paths

Ignore Extensions

Minimum Size

Age Threshold

Thread Count

Risk Overrides

========================================================
LOGGING
=======

Log

Start

Finish

Errors

Permissions

Skipped Files

Deleted Files

Duration

========================================================
DIAGNOSTICS
===========

Display

Elapsed Time

Memory Usage

Directories Per Second

Files Per Second

Bytes Per Second

========================================================
CODE QUALITY
============

Every function must have XML documentation.

Every module should begin with documentation.

Avoid duplication.

Use pipeline operators.

Use active patterns where appropriate.

Use discriminated unions extensively.

Use records rather than classes unless OO is clearly superior.

========================================================
OUTPUT
======

Produce a complete working .fsx script.

No pseudocode.

No TODOs.

No omitted sections.

No placeholders.

Everything should compile.

If the output becomes too large,

continue automatically into the next response,

preserving exact code continuity until the script is complete.
