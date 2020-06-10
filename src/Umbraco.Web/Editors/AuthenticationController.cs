using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Collections.Generic;
using System.Net.Mail;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Web;
using System.Web.Http;
using System.Web.Mvc;
using Microsoft.AspNetCore.Identity;
using Umbraco.Core;
using Umbraco.Core.Cache;
using Umbraco.Core.Models;
using Umbraco.Core.Services;
using Umbraco.Web.Models;
using Umbraco.Web.Models.ContentEditing;
using Umbraco.Web.Mvc;
using Umbraco.Web.Security;
using Umbraco.Web.WebApi;
using Umbraco.Web.WebApi.Filters;
using Umbraco.Core.Configuration;
using Umbraco.Core.Logging;
using Umbraco.Core.Persistence;
using IUser = Umbraco.Core.Models.Membership.IUser;
using Umbraco.Core.Mapping;
using Umbraco.Core.Configuration.UmbracoSettings;
using Umbraco.Core.Hosting;
using Umbraco.Extensions;
using Umbraco.Web.Routing;

namespace Umbraco.Web.Editors
{
    /// <summary>
    /// The API controller used for editing content
    /// </summary>
    [PluginController("UmbracoApi")]
    [ValidationFilter]
    [AngularJsonOnlyConfiguration]
    [IsBackOffice]
    public class AuthenticationController : UmbracoApiController
    {
        private BackOfficeOwinUserManager _userManager;
        private BackOfficeSignInManager _signInManager;
        private readonly IUserPasswordConfiguration _passwordConfiguration;
        private readonly IHostingEnvironment _hostingEnvironment;
        private readonly IRuntimeState _runtimeState;
        private readonly ISecuritySettings _securitySettings;
        private readonly IRequestAccessor _requestAccessor;
        private readonly IEmailSender _emailSender;

        public AuthenticationController(
            IUserPasswordConfiguration passwordConfiguration,
            IGlobalSettings globalSettings,
            IHostingEnvironment hostingEnvironment,
            IUmbracoContextAccessor umbracoContextAccessor,
            ISqlContext sqlContext,
            ServiceContext services,
            AppCaches appCaches,
            IProfilingLogger logger,
            IRuntimeState runtimeState,
            UmbracoMapper umbracoMapper,
            ISecuritySettings securitySettings,
            IPublishedUrlProvider publishedUrlProvider,
            IRequestAccessor requestAccessor,
            IEmailSender emailSender)
            : base(globalSettings, umbracoContextAccessor, sqlContext, services, appCaches, logger, runtimeState, umbracoMapper, publishedUrlProvider)
        {
            _passwordConfiguration = passwordConfiguration ?? throw new ArgumentNullException(nameof(passwordConfiguration));
            _hostingEnvironment = hostingEnvironment ?? throw new ArgumentNullException(nameof(hostingEnvironment));
            _runtimeState = runtimeState ?? throw new ArgumentNullException(nameof(runtimeState));
            _securitySettings = securitySettings ?? throw new ArgumentNullException(nameof(securitySettings));
            _requestAccessor = requestAccessor ?? throw new ArgumentNullException(nameof(securitySettings));
            _emailSender = emailSender;
        }

        protected BackOfficeOwinUserManager UserManager => _userManager
                                                           ?? (_userManager = TryGetOwinContext().Result.GetBackOfficeUserManager());

        protected BackOfficeSignInManager SignInManager => _signInManager
            ?? (_signInManager = TryGetOwinContext().Result.GetBackOfficeSignInManager());

        /// <summary>
        /// Returns the configuration for the backoffice user membership provider - used to configure the change password dialog
        /// </summary>
        /// <returns></returns>
        [WebApi.UmbracoAuthorize(requireApproval: false)]
        public IDictionary<string, object> GetPasswordConfig(int userId)
        {
            return _passwordConfiguration.GetConfiguration(userId != Security.CurrentUser.Id);
        }

        /// <summary>
        /// Checks if a valid token is specified for an invited user and if so logs the user in and returns the user object
        /// </summary>
        /// <param name="id"></param>
        /// <param name="token"></param>
        /// <returns></returns>
        /// <remarks>
        /// This will also update the security stamp for the user so it can only be used once
        /// </remarks>
        [ValidateAngularAntiForgeryToken]
        public async Task<UserDisplay> PostVerifyInvite([FromUri]int id, [FromUri]string token)
        {
            if (string.IsNullOrWhiteSpace(token))
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            var decoded = token.FromUrlBase64();
            if (decoded.IsNullOrWhiteSpace())
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            var identityUser = await UserManager.FindByIdAsync(id.ToString());
            if (identityUser == null)
                throw new HttpResponseException(Request.CreateResponse(HttpStatusCode.NotFound));

            var result = await UserManager.ConfirmEmailAsync(identityUser, decoded);

            if (result.Succeeded == false)
            {
                throw new HttpResponseException(Request.CreateNotificationValidationErrorResponse(result.Errors.ToErrorMessage()));
            }

            Request.TryGetOwinContext().Result.Authentication.SignOut(
                Core.Constants.Security.BackOfficeAuthenticationType,
                Core.Constants.Security.BackOfficeExternalAuthenticationType);

            await SignInManager.SignInAsync(identityUser, false, false);

            var user = Services.UserService.GetUserById(id);

            return Mapper.Map<UserDisplay>(user);
        }

        [WebApi.UmbracoAuthorize]
        [ValidateAngularAntiForgeryToken]
        public async Task<HttpResponseMessage> PostUnLinkLogin(UnLinkLoginModel unlinkLoginModel)
        {
            var user = await UserManager.FindByIdAsync(User.Identity.GetUserId());
            if (user == null) throw new InvalidOperationException("Could not find user");

            var result = await UserManager.RemoveLoginAsync(
                user,
                unlinkLoginModel.LoginProvider,
                unlinkLoginModel.ProviderKey);

            if (result.Succeeded)
            {
                await SignInManager.SignInAsync(user, isPersistent: true, rememberBrowser: false);
                return Request.CreateResponse(HttpStatusCode.OK);
            }
            else
            {
                AddModelErrors(result);
                return Request.CreateValidationErrorResponse(ModelState);
            }
        }


        /// <summary>
        /// When a user is invited they are not approved but we need to resolve the partially logged on (non approved)
        /// user.
        /// </summary>
        /// <returns></returns>
        /// <remarks>
        /// We cannot user GetCurrentUser since that requires they are approved, this is the same as GetCurrentUser but doesn't require them to be approved
        /// </remarks>
        [WebApi.UmbracoAuthorize(requireApproval: false)]
        [SetAngularAntiForgeryTokens]
        public UserDetail GetCurrentInvitedUser()
        {
            var user = Security.CurrentUser;

            if (user.IsApproved)
            {
                // if they are approved, than they are no longer invited and we can return an error
                throw new HttpResponseException(Request.CreateUserNoAccessResponse());
            }

            var result = Mapper.Map<UserDetail>(user);
            var httpContextAttempt = TryGetHttpContext();
            if (httpContextAttempt.Success)
            {
                // set their remaining seconds
                result.SecondsUntilTimeout = httpContextAttempt.Result.GetRemainingAuthSeconds();
            }

            return result;
        }

        // TODO: This should be on the CurrentUserController?
        [WebApi.UmbracoAuthorize]
        [ValidateAngularAntiForgeryToken]
        public async Task<Dictionary<string, string>> GetCurrentUserLinkedLogins()
        {
            var identityUser = await UserManager.FindByIdAsync(Security.GetUserId().ResultOr(0).ToString());
            return identityUser.Logins.ToDictionary(x => x.LoginProvider, x => x.ProviderKey);
        }


        /// <summary>
        /// Processes a password reset request.  Looks for a match on the provided email address
        /// and if found sends an email with a link to reset it
        /// </summary>
        /// <returns></returns>
        [SetAngularAntiForgeryTokens]
        public async Task<HttpResponseMessage> PostRequestPasswordReset(RequestPasswordResetModel model)
        {
            // If this feature is switched off in configuration the UI will be amended to not make the request to reset password available.
            // So this is just a server-side secondary check.
            if (_securitySettings.AllowPasswordReset == false)
            {
                throw new HttpResponseException(HttpStatusCode.BadRequest);
            }
            var identityUser = await UserManager.FindByEmailAsync(model.Email);
            if (identityUser != null)
            {
                var user = Services.UserService.GetByEmail(model.Email);
                if (user != null)
                {
                    var code = await UserManager.GeneratePasswordResetTokenAsync(identityUser);
                    var callbackUrl = ConstructCallbackUrl(identityUser.Id, code);

                    var message = Services.TextService.Localize("resetPasswordEmailCopyFormat",
                        // Ensure the culture of the found user is used for the email!
                        UmbracoUserExtensions.GetUserCulture(identityUser.Culture, Services.TextService, GlobalSettings),
                        new[] { identityUser.UserName, callbackUrl });

                    var subject = Services.TextService.Localize("login/resetPasswordEmailCopySubject",
                        // Ensure the culture of the found user is used for the email!
                        UmbracoUserExtensions.GetUserCulture(identityUser.Culture, Services.TextService, GlobalSettings));

                    var mailMessage = new MailMessage()
                    {
                        Subject = subject,
                        Body = message,
                        IsBodyHtml = true,
                        To = { user.Email}
                    };

                    await _emailSender.SendAsync(mailMessage);

                    UserManager.RaiseForgotPasswordRequestedEvent(User, user.Id);
                }
            }

            return Request.CreateResponse(HttpStatusCode.OK);
        }

        /// <summary>
        /// Used to retrieve the 2FA providers for code submission
        /// </summary>
        /// <returns></returns>
        [SetAngularAntiForgeryTokens]
        public async Task<IEnumerable<string>> Get2FAProviders()
        {
            var userId = await SignInManager.GetVerifiedUserIdAsync();
            if (string.IsNullOrWhiteSpace(userId))
            {
                Logger.Warn<AuthenticationController>("Get2FAProviders :: No verified user found, returning 404");
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var user = await UserManager.FindByIdAsync(userId);
            var userFactors = await UserManager.GetValidTwoFactorProvidersAsync(user);

            return userFactors;
        }

        [SetAngularAntiForgeryTokens]
        public async Task<IHttpActionResult> PostSend2FACode([FromBody]string provider)
        {
            if (provider.IsNullOrWhiteSpace())
                throw new HttpResponseException(HttpStatusCode.NotFound);

            var userId = await SignInManager.GetVerifiedUserIdAsync();
            if (string.IsNullOrWhiteSpace(userId))
            {
                Logger.Warn<AuthenticationController>("Get2FAProviders :: No verified user found, returning 404");
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            // Generate the token and send it
            if (await SignInManager.SendTwoFactorCodeAsync(provider) == false)
            {
                return BadRequest("Invalid code");
            }
            return Ok();
        }

        [SetAngularAntiForgeryTokens]
        public async Task<HttpResponseMessage> PostVerify2FACode(Verify2FACodeModel model)
        {
            if (ModelState.IsValid == false)
            {
                return Request.CreateValidationErrorResponse(ModelState);
            }

            var userName = await SignInManager.GetVerifiedUserNameAsync();
            if (userName == null)
            {
                Logger.Warn<AuthenticationController>("Get2FAProviders :: No verified user found, returning 404");
                throw new HttpResponseException(HttpStatusCode.NotFound);
            }

            var result = await SignInManager.TwoFactorSignInAsync(model.Provider, model.Code, isPersistent: true, rememberBrowser: false);
            var owinContext = TryGetOwinContext().Result;

            var user = Services.UserService.GetByUsername(userName);
            if (result.Succeeded)
            {
                return SetPrincipalAndReturnUserDetail(user, owinContext.Request.User);
            }

            if (result.IsLockedOut)
            {
                UserManager.RaiseAccountLockedEvent(User, user.Id);
                return Request.CreateValidationErrorResponse("User is locked out");
            }

            return Request.CreateValidationErrorResponse("Invalid code");
        }

        /// <summary>
        /// Processes a set password request.  Validates the request and sets a new password.
        /// </summary>
        /// <returns></returns>
        [SetAngularAntiForgeryTokens]
        public async Task<HttpResponseMessage> PostSetPassword(SetPasswordModel model)
        {
            var identityUser = await UserManager.FindByIdAsync(model.UserId.ToString());

            var result = await UserManager.ResetPasswordAsync(identityUser, model.ResetCode, model.Password);
            if (result.Succeeded)
            {
                var lockedOut = await UserManager.IsLockedOutAsync(identityUser);
                if (lockedOut)
                {
                    Logger.Info<AuthenticationController>("User {UserId} is currently locked out, unlocking and resetting AccessFailedCount", model.UserId);

                    //// var user = await UserManager.FindByIdAsync(model.UserId);
                    var unlockResult = await UserManager.SetLockoutEndDateAsync(identityUser, DateTimeOffset.Now);
                    if (unlockResult.Succeeded == false)
                    {
                        Logger.Warn<AuthenticationController>("Could not unlock for user {UserId} - error {UnlockError}", model.UserId, unlockResult.Errors.First().Description);
                    }

                    var resetAccessFailedCountResult = await UserManager.ResetAccessFailedCountAsync(identityUser);
                    if (resetAccessFailedCountResult.Succeeded == false)
                    {
                        Logger.Warn<AuthenticationController>("Could not reset access failed count {UserId} - error {UnlockError}", model.UserId, unlockResult.Errors.First().Description);
                    }
                }

                // They've successfully set their password, we can now update their user account to be confirmed
                // if user was only invited, then they have not been approved
                // but a successful forgot password flow (e.g. if their token had expired and they did a forgot password instead of request new invite)
                // means we have verified their email
                if (!await UserManager.IsEmailConfirmedAsync(identityUser))
                {
                    await UserManager.ConfirmEmailAsync(identityUser, model.ResetCode);
                }

                // invited is not approved, never logged in, invited date present
                /*
                if (LastLoginDate == default && IsApproved == false && InvitedDate != null)
                    return UserState.Invited;
                */
                if (identityUser != null && !identityUser.IsApproved)
                {
                    var user = Services.UserService.GetByUsername(identityUser.UserName);
                    // also check InvitedDate and never logged in, otherwise this would allow a disabled user to reactivate their account with a forgot password
                    if (user.LastLoginDate == default && user.InvitedDate != null)
                    {
                        user.IsApproved = true;
                        user.InvitedDate = null;
                        Services.UserService.Save(user);
                    }
                }

                UserManager.RaiseForgotPasswordChangedSuccessEvent(User, model.UserId);
                return Request.CreateResponse(HttpStatusCode.OK);
            }
            return Request.CreateValidationErrorResponse(
                result.Errors.Any() ? result.Errors.First().Description : "Set password failed");
        }


        /// <summary>
        /// Logs the current user out
        /// </summary>
        /// <returns></returns>
        [ClearAngularAntiForgeryToken]
        [ValidateAngularAntiForgeryToken]
        public HttpResponseMessage PostLogout()
        {
            var owinContext = Request.TryGetOwinContext().Result;

            owinContext.Authentication.SignOut(
                Core.Constants.Security.BackOfficeAuthenticationType,
                Core.Constants.Security.BackOfficeExternalAuthenticationType);

            Logger.Info<AuthenticationController>("User {UserName} from IP address {RemoteIpAddress} has logged out", User.Identity == null ? "UNKNOWN" : User.Identity.Name, owinContext.Request.RemoteIpAddress);

            if (UserManager != null)
            {
                int.TryParse(User.Identity.GetUserId(), out var userId);
                UserManager.RaiseLogoutSuccessEvent(User, userId);
            }

            return Request.CreateResponse(HttpStatusCode.OK);
        }

        // NOTE: This has been migrated to netcore, but in netcore we don't explicitly set the principal in this method, that's done in ConfigureUmbracoBackOfficeCookieOptions so don't worry about that
        private HttpResponseMessage SetPrincipalAndReturnUserDetail(IUser user, IPrincipal principal)
        {
            throw new NotImplementedException();
        }

        private string ConstructCallbackUrl(int userId, string code)
        {
            // Get an mvc helper to get the url
            var http = EnsureHttpContext();
            var urlHelper = new UrlHelper(http.Request.RequestContext);
            var action = urlHelper.Action("ValidatePasswordResetCode", "BackOffice",
                new
                {
                    area = GlobalSettings.GetUmbracoMvcArea(_hostingEnvironment),
                    u = userId,
                    r = code
                });

            // Construct full URL using configured application URL (which will fall back to request)
            var applicationUri = _requestAccessor.GetApplicationUrl();
            var callbackUri = new Uri(applicationUri, action);
            return callbackUri.ToString();
        }


        private HttpContextBase EnsureHttpContext()
        {
            var attempt = this.TryGetHttpContext();
            if (attempt.Success == false)
                throw new InvalidOperationException("This method requires that an HttpContext be active");
            return attempt.Result;
        }



        private void AddModelErrors(IdentityResult result, string prefix = "")
        {
            foreach (var error in result.Errors)
            {
                ModelState.AddModelError(prefix, error.Description);
            }
        }
    }
}
