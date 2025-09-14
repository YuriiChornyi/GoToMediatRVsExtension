# GitHub Workflows for MediatR Navigation Extension

This directory contains GitHub Actions workflows for automated building, testing, and publishing of the MediatR Navigation Extension.

## 🚀 Workflows Overview

### 1. **Build and Test** (`build-and-publish.yml`)
- **Triggers**: Push to main, tags, PRs, manual dispatch
- **Purpose**: CI/CD pipeline for building and testing only
- **Features**:
  - Builds VSIX package
  - Runs integrity tests
  - Uploads build artifacts
  - No publishing (handled by separate workflows)

### 2. **Version Bump** (`version-bump.yml`)
- **Triggers**: Manual dispatch only
- **Purpose**: Automated version management and release creation
- **Features**:
  - Semantic version bumping (major/minor/patch)
  - Custom version support
  - Automatic git tagging
  - Draft release creation with README-based notes

### 3. **Marketplace Publish** (`marketplace-publish.yml`)
- **Triggers**: GitHub releases (published), manual dispatch
- **Purpose**: Publishes to Visual Studio Marketplace using VsixPublisher.exe
- **Features**:
  - Automated marketplace publishing on release
  - Uses custom publishManifest.json
  - Attaches VSIX to GitHub releases
  - Comprehensive error handling and validation

## ⚙️ Setup Instructions

### 1. **Repository Secrets**

Add these secrets to your GitHub repository (`Settings` → `Secrets and variables` → `Actions`):

#### Required Secrets:
```
MARKETPLACE_PAT
```
- **Description**: Personal Access Token for Visual Studio Marketplace
- **How to get**: 
  1. Go to https://marketplace.visualstudio.com/manage
  2. Create a new organization or use existing
  3. Generate a PAT with `Marketplace (publish)` scope
  4. Copy the token to this secret

#### Notes:
- Only `MARKETPLACE_PAT` is required
- Uses Azure DevOps Personal Access Token with `Marketplace (Publish)` scope
- No other secrets needed for the current setup

### 2. **Environment Protection**

Set up a `production` environment for marketplace publishing:

1. Go to `Settings` → `Environments`
2. Create environment named `production`
3. Add protection rules:
   - Required reviewers (recommended)
   - Deployment branches (only main and tags)

### 3. **Branch Protection**

Configure branch protection for main:

1. Go to `Settings` → `Branches`
2. Add rule for main:
   - Require status checks: `validate-pr`
   - Require up-to-date branches
   - Require signed commits (optional)

## 🎯 Usage Guide

### **Automated Workflows**

1. **Normal Development**:
   - Create feature branch
   - Make changes
   - Open PR → Triggers `pr-validation.yml`
   - Merge PR → Triggers `build-and-publish.yml`

2. **Release Process** (Recommended):
   ```bash
   # Step 1: Use Version Bump workflow
   # Go to Actions → Version Bump → Run workflow
   # Select version type (patch/minor/major)
   # This creates a DRAFT release
   
   # Step 2: Publish the draft release
   # Go to Releases → Edit draft → Publish release
   # This automatically triggers marketplace publishing
   ```

3. **Alternative Release Process**:
   ```bash
   # Manual approach (not recommended)
   git tag v6.5.0
   git push origin v6.5.0
   # Then manually create release from tag
   ```

### **Manual Workflows**

#### Version Bump:
```
Actions → Version Bump → Run workflow
├── Version Type: patch/minor/major
└── Custom Version: (optional) e.g., "6.5.0"
```

#### Force Marketplace Publish:
```
Actions → Publish to Visual Studio Marketplace → Run workflow
└── Release Tag: e.g., "v6.5.0"
```

#### Force Build:
```
Actions → Build and Test → Run workflow
└── (No parameters - builds and tests only)
```

## 📦 Artifacts

### **Build Artifacts**
- **VSIX Package**: Ready-to-install extension file
- **Build Logs**: Available on build failures
- **Retention**: 90 days for VSIX, 7 days for logs

### **Release Assets**
- **GitHub Releases**: Include VSIX file and release notes
- **Marketplace**: Automatic publication on tagged releases

## 🔧 Customization

### **Build Configuration**

Edit `build-and-publish.yml`:
```yaml
env:
  SOLUTION_FILE: VSIXExtention.sln  # Your solution file
  BUILD_CONFIGURATION: Release      # Build configuration
  BUILD_PLATFORM: Any CPU          # Target platform
```

### **Version Pattern**

The workflows expect semantic versioning (X.Y.Z):
- **Major**: Breaking changes
- **Minor**: New features
- **Patch**: Bug fixes

### **Marketplace Settings**

Customize marketplace publication in `marketplace-publish.yml`:
- Update API endpoints if needed
- Modify release notes format
- Add additional metadata updates

## 🚨 Troubleshooting

### **Common Issues**

1. **Build Failures**:
   - Check NuGet package restoration
   - Verify .NET Framework version
   - Review build logs in artifacts

2. **Marketplace Publishing Fails**:
   - Verify `MARKETPLACE_PAT` secret
   - Check token permissions
   - Validate VSIX integrity

3. **Version Conflicts**:
   - Ensure version is unique
   - Check existing git tags
   - Verify manifest version format

### **Debug Steps**

1. **Enable Debug Logging**:
   ```yaml
   - name: Enable debug
     run: echo "ACTIONS_RUNNER_DEBUG=true" >> $GITHUB_ENV
   ```

2. **Manual VSIX Testing**:
   - Download artifact from workflow
   - Test installation in VS
   - Verify functionality

3. **Marketplace Validation**:
   - Check marketplace dashboard
   - Review publication logs
   - Verify extension appears in search

## 📋 Checklist for First Setup

- [ ] Add `MARKETPLACE_PAT` secret
- [ ] Create `production` environment
- [ ] Configure branch protection
- [ ] Test PR validation workflow
- [ ] Run version bump workflow
- [ ] Verify build and publish workflow
- [ ] Test marketplace publishing
- [ ] Validate GitHub releases

## 🎉 Success Indicators

When everything is working correctly:

1. ✅ PRs trigger validation automatically
2. ✅ Tags create GitHub releases with VSIX files
3. ✅ Releases publish to marketplace automatically
4. ✅ Version bumping creates proper tags and releases
5. ✅ Build artifacts are available for manual testing

Your extension will now have a complete CI/CD pipeline! 🚀
