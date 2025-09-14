# GitHub Workflows for MediatR Navigation Extension

This directory contains GitHub Actions workflows for automated building, testing, and publishing of the MediatR Navigation Extension.

## 🚀 Workflows Overview

### 1. **Build and Publish** (`build-and-publish.yml`)
- **Triggers**: Push to main, tags, PRs, manual dispatch
- **Purpose**: Main CI/CD pipeline for building and testing
- **Features**:
  - Builds VSIX package
  - Runs integrity tests
  - Creates GitHub releases for tags
  - Uploads build artifacts

### 2. **Marketplace Publish** (`marketplace-publish.yml`)
- **Triggers**: GitHub releases, manual dispatch
- **Purpose**: Publishes to Visual Studio Marketplace
- **Features**:
  - Automated marketplace publishing
  - Version validation
  - Release notes integration

### 3. **Version Bump** (`version-bump.yml`)
- **Triggers**: Manual dispatch only
- **Purpose**: Automated version management
- **Features**:
  - Semantic version bumping (major/minor/patch)
  - Custom version support
  - Automatic git tagging
  - Draft release creation

### 4. **PR Validation** (`pr-validation.yml`)
- **Triggers**: Pull requests to main
- **Purpose**: Validate PRs before merging
- **Features**:
  - Build validation
  - Static code analysis
  - Manifest validation
  - Version conflict detection

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

#### Optional Secrets:
```
VSCE_PAT
```
- **Description**: Alternative publishing token (if using different method)
- **Note**: Currently used as fallback, may not be needed

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

2. **Release Process**:
   ```bash
   # Option 1: Manual version bump
   # Go to Actions → Version Bump → Run workflow
   # Select version type (patch/minor/major)
   
   # Option 2: Manual tagging
   git tag v6.5.0
   git push origin v6.5.0
   ```

3. **Publishing to Marketplace**:
   - Create GitHub release → Triggers `marketplace-publish.yml`
   - Or manually dispatch `marketplace-publish.yml`

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
Actions → Build and Publish → Run workflow
└── Publish to Marketplace: true/false
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
