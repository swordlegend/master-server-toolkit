﻿using MasterServerToolkit.Logging;
using MasterServerToolkit.Networking;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;

namespace MasterServerToolkit.MasterServer
{
    public delegate void UserLoggedInEventHandlerDelegate(IUserPeerExtension user);
    public delegate void UserLoggedOutEventHandlerDelegate(IUserPeerExtension user);
    public delegate void UserRegisteredEventHandlerDelegate(IPeer peer, IAccountInfoData account);
    public delegate void UserEmailConfirmedEventHandlerDelegate(IAccountInfoData account);
    public delegate void UserAccountUpdatedEventHandlerDelegate(IAccountInfoData account);
    public delegate void UsernameChangedEventHandlerDelegate(string oldUsername, string newUsername);

    /// <summary>
    /// Authentication module, which handles logging in and registration of accounts
    /// </summary>
    public class AuthModule : BaseServerModule
    {
        #region INSPECTOR

        [Header("Settings"), SerializeField, Tooltip("Min number of username characters. Will not be used in guest username")]
        protected int usernameMinChars = 4;

        [SerializeField, Tooltip("Max number of username characters. Will not be used in guest username")]
        protected int usernameMaxChars = 12;

        [SerializeField, Tooltip("Min number of user password characters")]
        protected int userPasswordMinChars = 8;

        [SerializeField, Tooltip("Whether or not to use email confirmation when sign up")]
        protected bool useEmailConfirmation = true;

        [Header("Guest Settings")]
        [SerializeField, Tooltip("If true, players will be able to log in as guests")]
        protected bool enableGuestLogin = true;

        [SerializeField, Tooltip("Guest names will start with this prefix")]
        protected string guestPrefix = "user_";

        [SerializeField, Tooltip("Whether or not to save guest info")]
        protected bool saveGuestInfo = false;

        [Header("Permission Settings"), SerializeField, Tooltip("How many days token will be valid before expire. The token will also be updated each login")]
        protected byte tokenExpiresInDays = 60;

        [SerializeField, Tooltip("Minimal permission level, required to retrieve peer account information")]
        protected int getPeerDataPermissionsLevel = 0;

        [Header("E-Mail Settings"), SerializeField]
        protected Mailer mailer;

        [SerializeField, TextArea(3, 10)]
        public string emailAddressValidationTemplate = @"^[a-z0-9][-a-z0-9._]+@([-a-z0-9]+\.)+[a-z]{2,5}$";

        [Header("Generic"), SerializeField, Tooltip("Min number of characters the service code must contain")]
        protected int serviceCodeMinChars = 6;

        #endregion

        /// <summary>
        /// Censor module for bad words checking :)
        /// </summary>
        protected CensorModule censorModule;

        /// <summary>
        /// Collection of users who are currently logged in by user id
        /// </summary>
        protected Dictionary<string, IUserPeerExtension> loggedInUsersById { get; set; }

        /// <summary>
        /// Collection of users who are currently logged in by username
        /// </summary>
        protected Dictionary<string, IUserPeerExtension> loggedInUsersByUsername { get; set; }

        /// <summary>
        /// Collection of users who are currently logged in
        /// </summary>
        public IEnumerable<IUserPeerExtension> LoggedInUsers => loggedInUsersById.Values;

        /// <summary>
        /// Whether or not to save guest info
        /// </summary>
        public bool SaveGuestInfo => saveGuestInfo;

        /// <summary>
        /// Invoked, when user logedin
        /// </summary>
        public event UserLoggedInEventHandlerDelegate OnUserLoggedInEvent;

        /// <summary>
        /// Invoked, when user logs out
        /// </summary>
        public event UserLoggedOutEventHandlerDelegate OnUserLoggedOutEvent;

        /// <summary>
        /// Invoked, when user successfully registers an account
        /// </summary>
        public event UserRegisteredEventHandlerDelegate OnUserRegisteredEvent;

        /// <summary>
        /// Invoked, when user successfully confirms his e-mail
        /// </summary>
        public event UserEmailConfirmedEventHandlerDelegate OnUserEmailConfirmedEvent;

        /// <summary>
        /// Invoked, when user successfully updated his account
        /// </summary>
        public event UserAccountUpdatedEventHandlerDelegate OnUserAccountUpdatedEvent;

        /// <summary>
        /// Invoked, when user successfully changed his username
        /// </summary>
        public event UsernameChangedEventHandlerDelegate OnUsernameChangedEvent;

        protected override void Awake()
        {
            base.Awake();

            // Optional dependancy to CensorModule
            AddOptionalDependency<CensorModule>();
        }

        protected virtual void OnValidate()
        {
            if (usernameMaxChars <= usernameMinChars)
            {
                usernameMaxChars = usernameMinChars + 1;
            }

            if (tokenExpiresInDays < 0)
            {
                tokenExpiresInDays = 1;
            }
        }

        public override void Initialize(IServer server)
        {
            censorModule = server.GetModule<CensorModule>();
            mailer = mailer ?? FindObjectOfType<Mailer>();

            // Init logged in users list by id
            loggedInUsersById = new Dictionary<string, IUserPeerExtension>();
            loggedInUsersByUsername = new Dictionary<string, IUserPeerExtension>();

            // Set handlers
            server.RegisterMessageHandler((short)MstMessageCodes.SignIn, SignInMessageHandler);
            server.RegisterMessageHandler((short)MstMessageCodes.SignUp, SignUpMessageHandler);

            server.RegisterMessageHandler((short)MstMessageCodes.GetPasswordResetCode, GetPasswordResetCodeMessageHandler);
            server.RegisterMessageHandler((short)MstMessageCodes.ChangePassword, ChangePasswordMessageHandler);

            server.RegisterMessageHandler((short)MstMessageCodes.GetEmailConfirmationCode, GetEmailConfirmationCodeMessageHandler);
            server.RegisterMessageHandler((short)MstMessageCodes.ConfirmEmail, ConfirmEmailMessageHandler);

            server.RegisterMessageHandler((short)MstMessageCodes.GetLoggedInUsersCount, GetLoggedInUsersCountMessageHandler);

            server.RegisterMessageHandler((short)MstMessageCodes.GetPeerAccountInfo, GetPeerAccountInfoMessageHandler);

            server.RegisterMessageHandler((short)MstMessageCodes.UpdateAccountInfo, UpdateAccountInfoMessageHandler);
        }

        /// <summary>
        /// Generates guest username
        /// </summary>
        /// <returns></returns>
        protected virtual string GenerateGuestUsername()
        {
            string prefix = string.IsNullOrEmpty(guestPrefix) ? "user_" : guestPrefix;
            return $"{prefix}{Mst.Helper.CreateID_16()}";
        }

        /// <summary>
        /// Notify when user logged in
        /// </summary>
        /// <param name="user"></param>
        public virtual void NotifyOnUserLoggedInEvent(IUserPeerExtension user)
        {
            OnUserLoggedInEvent?.Invoke(user);
        }

        /// <summary>
        /// Get logged in user by Username
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        public IUserPeerExtension GetLoggedInUserByUsername(string username)
        {
            if (loggedInUsersByUsername.TryGetValue(username.ToLower(), out IUserPeerExtension user))
            {
                return user;
            }
            else
            {
                return default;
            }
        }

        /// <summary>
        /// Get logged in user by id
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public IUserPeerExtension GetLoggedInUserById(string id)
        {
            if (loggedInUsersById.TryGetValue(id, out IUserPeerExtension user))
            {
                return user;
            }
            else
            {
                return default;
            }
        }

        /// <summary>
        /// Check if given user is logged in
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        public bool IsUserLoggedInByUsername(string username)
        {
            return loggedInUsersByUsername.ContainsKey(username.ToLower());
        }

        /// <summary>
        /// Check if given user is logged in
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        public bool IsUserLoggedInById(string id)
        {
            return loggedInUsersById.ContainsKey(id);
        }

        /// <summary>
        /// Check if given peer has permission to get peer info
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        public bool HasGetPeerInfoPermissions(IPeer peer)
        {
            var extension = peer.GetExtension<SecurityInfoPeerExtension>();
            return extension.PermissionLevel >= getPeerDataPermissionsLevel;
        }

        /// <summary>
        /// Create instance of <see cref="UserPeerExtension"/>
        /// </summary>
        /// <param name="peer"></param>
        /// <returns></returns>
        public virtual IUserPeerExtension CreateUserPeerExtension(IPeer peer)
        {
            return new UserPeerExtension(peer);
        }

        /// <summary>
        /// Fired when any user disconected from server
        /// </summary>
        /// <param name="peer"></param>
        protected virtual void OnUserDisconnectedEventListener(IPeer peer)
        {
            peer.OnPeerDisconnectedEvent -= OnUserDisconnectedEventListener;

            var extension = peer.GetExtension<IUserPeerExtension>();

            if (extension == null)
            {
                return;
            }

            loggedInUsersByUsername.Remove(extension.Username.ToLower());
            loggedInUsersById.Remove(extension.UserId);

            OnUserLoggedOutEvent?.Invoke(extension);
        }

        /// <summary>
        /// Check if Username is valid. Whether it is not empty or has no white spaces
        /// </summary>
        /// <param name="username"></param>
        /// <returns></returns>
        protected virtual bool IsUsernameValid(string username)
        {
            string lowerUserName = username?.ToLower();
            return !string.IsNullOrEmpty(lowerUserName?.Trim()) && !lowerUserName.Contains(" ");
        }

        /// <summary>
        /// Check if Email is valid
        /// </summary>
        /// <param name="email"></param>
        /// <returns></returns>
        protected virtual bool IsEmailValid(string email)
        {
            return !string.IsNullOrEmpty(email.Trim()) && Regex.IsMatch(email, emailAddressValidationTemplate);
        }

        #region MESSAGE HANDLERS

        /// <summary>
        /// Handles client's request to change password
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void ChangePasswordMessageHandler(IIncomingMessage message)
        {
            var data = new Dictionary<string, string>().FromBytes(message.AsBytes());

            if (!data.ContainsKey("code") || !data.ContainsKey("password") || !data.ContainsKey("email"))
            {
                message.Respond("Invalid request", ResponseStatus.Unauthorized);
                return;
            }

            var authDbAccessor = Mst.Server.DbAccessors.GetAccessor<IAccountsDatabaseAccessor>();
            var passwordResetData = await authDbAccessor.GetPasswordResetDataAsync(data["email"]);

            if (passwordResetData == null || passwordResetData.Code == null || passwordResetData.Code != data["code"])
            {
                message.Respond("Invalid code provided", ResponseStatus.Unauthorized);
                return;
            }

            var account = await authDbAccessor.GetAccountByEmailAsync(data["email"]);

            // Delete (overwrite) code used
            await authDbAccessor.SavePasswordResetCodeAsync(account, null);

            account.Password = Mst.Security.CreateHash(data["password"]);
            await authDbAccessor.UpdateAccountAsync(account);

            message.Respond(ResponseStatus.Success);
        }

        /// <summary>
        /// Handles a request to retrieve a number of logged in users
        /// </summary>
        /// <param name="message"></param>
        protected virtual void GetLoggedInUsersCountMessageHandler(IIncomingMessage message)
        {
            message.Respond(loggedInUsersById.Count, ResponseStatus.Success);
        }

        /// <summary>
        /// Handles password reset request
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void GetPasswordResetCodeMessageHandler(IIncomingMessage message)
        {
            var userEmail = message.AsString();
            var authDbAccessor = Mst.Server.DbAccessors.GetAccessor<IAccountsDatabaseAccessor>();
            var userAccount = await authDbAccessor.GetAccountByEmailAsync(userEmail);

            if (userAccount == null)
            {
                message.Respond("No such e-mail in the system", ResponseStatus.Unauthorized);
                return;
            }

            var passwordResetCode = Mst.Helper.CreateRandomAlphanumericString(serviceCodeMinChars);
            await authDbAccessor.SavePasswordResetCodeAsync(userAccount, passwordResetCode);

            StringBuilder emailBody = new StringBuilder();
            emailBody.Append($"<h3>You have requested reset password</h3>");
            emailBody.Append($"<p>Here is your reset code</p>");
            emailBody.Append($"<h1>{passwordResetCode}</h1>");
            emailBody.Append($"<p>Copy this code and paste it to your reset password form</p>");

            bool sentResult = await mailer.SendMailAsync(userAccount.Email, "Password Reset Code", emailBody.ToString());

            if (!sentResult)
            {
                message.Respond("Couldn't send an activation code to your e-mail");
                return;
            }

            message.Respond(ResponseStatus.Success);
        }

        /// <summary>
        /// Handles e-mail confirmation request
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void ConfirmEmailMessageHandler(IIncomingMessage message)
        {
            var confirmationCode = message.AsString();
            var userExtension = message.Peer.GetExtension<IUserPeerExtension>();

            if (userExtension == null || userExtension.Account == null)
            {
                message.Respond("Invalid session", ResponseStatus.Unauthorized);
                return;
            }

            if (userExtension.Account.IsGuest)
            {
                message.Respond("Guests cannot confirm e-mails", ResponseStatus.Unauthorized);
                return;
            }

            if (userExtension.Account.IsEmailConfirmed)
            {
                message.Respond("Your email is already confirmed", ResponseStatus.Success);
                return;
            }

            var authDbAccessor = Mst.Server.DbAccessors.GetAccessor<IAccountsDatabaseAccessor>();
            var requiredCode = await authDbAccessor.GetEmailConfirmationCodeAsync(userExtension.Account.Email);

            if (requiredCode != confirmationCode)
            {
                message.Respond("Invalid activation code", ResponseStatus.Error);
                return;
            }

            // Confirm e-mail
            userExtension.Account.IsEmailConfirmed = true;

            // Update account
            await authDbAccessor.UpdateAccountAsync(userExtension.Account);

            // Respond with success
            message.Respond(ResponseStatus.Success);

            // Invoke the event
            OnUserEmailConfirmedEvent?.Invoke(userExtension.Account);
        }

        /// <summary>
        /// Handles request to get email conformation code
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void GetEmailConfirmationCodeMessageHandler(IIncomingMessage message)
        {
            var userExtension = message.Peer.GetExtension<IUserPeerExtension>();

            if (userExtension == null || userExtension.Account == null)
            {
                message.Respond("Invalid session", ResponseStatus.Unauthorized);
                return;
            }

            if (userExtension.Account.IsGuest)
            {
                message.Respond("Guests cannot confirm e-mails", ResponseStatus.Unauthorized);
                return;
            }

            var newEmailConfirmationCode = Mst.Helper.CreateRandomAlphanumericString(serviceCodeMinChars);
            var authDbAccessor = Mst.Server.DbAccessors.GetAccessor<IAccountsDatabaseAccessor>();

            await authDbAccessor.SaveEmailConfirmationCodeAsync(userExtension.Account.Email, newEmailConfirmationCode);

            if (mailer == null)
            {
                message.Respond("Couldn't send a confirmation code to your e-mail. Please contact support", ResponseStatus.Error);
                return;
            }

            StringBuilder emailBody = new StringBuilder();
            emailBody.Append($"<h3>You have requested email activation</h3>");
            emailBody.Append($"<p>Here is your email activation code</p>");
            emailBody.Append($"<h1>{newEmailConfirmationCode}</h1>");
            emailBody.Append($"<p>Copy this code and paste it to your account activation form</p>");

            bool sentResult = await mailer.SendMailAsync(userExtension.Account.Email, "E-mail confirmation", emailBody.ToString());

            if (!sentResult)
            {
                message.Respond("Couldn't send a confirmation code to your e-mail. Please contact support", ResponseStatus.Error);
                return;
            }

            // Respond with success
            message.Respond(ResponseStatus.Success);
        }

        /// <summary>
        /// Handles a request to retrieve account information
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void GetPeerAccountInfoMessageHandler(IIncomingMessage message)
        {
            if (!HasGetPeerInfoPermissions(message.Peer))
            {
                message.Respond("Unauthorized", ResponseStatus.Unauthorized);
                return;
            }

            var peerId = message.AsInt();
            var peer = Server.GetPeer(peerId);

            if (peer == null)
            {
                message.Respond("Peer with a given ID is not in the game", ResponseStatus.Error);
                return;
            }

            var userExtension = peer.GetExtension<IUserPeerExtension>();

            if (userExtension == null)
            {
                message.Respond("Peer has not been authenticated", ResponseStatus.Failed);
                return;
            }

            var userAccount = userExtension.Account;

            var userAccountPacket = new PeerAccountInfoPacket()
            {
                PeerId = peerId,
                Properties = new MstProperties(userAccount.Properties),
                Username = userExtension.Username,
                UserId = userExtension.UserId
            };

            // This will help to know if current user is guest
            userAccountPacket.Properties.Set(MstDictKeys.USER_IS_GUEST, userAccount.IsGuest);

            message.Respond(userAccountPacket, ResponseStatus.Success);

            await Task.Delay(0);
        }

        /// <summary>
        /// Handle update properties request handler
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void UpdateAccountInfoMessageHandler(IIncomingMessage message)
        {
            message.Respond(ResponseStatus.Success);

            logger.Debug($"Changing user {message.Peer.Id} properties");

            var encryptedData = message.AsBytes();
            var securityExt = message.Peer.GetExtension<SecurityInfoPeerExtension>();
            var aesKey = securityExt.AesKey;

            if (aesKey == null)
            {
                // There's no aesKey that client and master agreed upon
                message.Respond("Insecure request", ResponseStatus.Unauthorized);
                return;
            }

            var decryptedBytesData = Mst.Security.DecryptAES(encryptedData, aesKey);
            var userUpdatePropertiesInfo = SerializablePacket.FromBytes(decryptedBytesData, new UpdateAccountInfoPacket());
            var authDbAccessor = Mst.Server.DbAccessors.GetAccessor<IAccountsDatabaseAccessor>();

            if (string.IsNullOrEmpty(userUpdatePropertiesInfo.Id))
            {
                logger.Debug("No user found to change his properties");
                message.Respond("Invalid request", ResponseStatus.Unauthorized);
                return;
            }

            // Trying to get user account by user id
            IAccountInfoData userAccount = await authDbAccessor.GetAccountByIdAsync(userUpdatePropertiesInfo.Id);

            // if no account found
            if (userAccount == null)
            {
                message.Respond("Invalid request", ResponseStatus.Unauthorized);
                return;
            }

            // Check if new username is valid
            if (IsUsernameValid(userUpdatePropertiesInfo.Username))
            {
                // Set new username
                userAccount.Username = userUpdatePropertiesInfo.Username.Trim();
            }

            // Check if new password is valid
            if (!string.IsNullOrEmpty(userUpdatePropertiesInfo.Password.Trim()))
            {
                // Set new password
                userAccount.Password = Mst.Security.CreateHash(userAccount.Password.Trim());
            }

            // Check if new email is valid
            if (IsEmailValid(userUpdatePropertiesInfo.Email) && userAccount.Email != userUpdatePropertiesInfo.Email)
            {
                // Set new email
                userAccount.Email = userUpdatePropertiesInfo.Email;
                // Set email confirmation status
                userAccount.IsEmailConfirmed = !useEmailConfirmation;
            }

            // If username and password are set so we are not a guest user!
            if (!string.IsNullOrEmpty(userAccount.Username) && !string.IsNullOrEmpty(userAccount.Password))
            {
                userAccount.IsGuest = false;
            }
            // If email and password are set so we are not a guest user!
            else if (IsEmailValid(userAccount.Email) && !string.IsNullOrEmpty(userAccount.Password))
            {
                userAccount.IsGuest = false;
            }

            // Set phone number
            userAccount.PhoneNumber = userUpdatePropertiesInfo.PhoneNumber;

            // Set facebook Id
            userAccount.Facebook = userUpdatePropertiesInfo.Facebook;

            // Set another custom properties
            userAccount.Properties = userUpdatePropertiesInfo.Properties.ToDictionary();

            // Trying to update account info data
            bool updateResult = await authDbAccessor.UpdateAccountAsync(userAccount);

            if (!updateResult)
            {
                message.Respond("An error occurred when updating the user's account", ResponseStatus.Error);
                return;
            }

            logger.Debug($"User {message.Peer.Id} has changed his account info successfully");

            // Send response to client
            message.Respond("Account properties have been successfully updated", ResponseStatus.Success);

            userAccount.MarkAsDirty();

            OnUserAccountUpdatedEvent?.Invoke(userAccount);
        }

        /// <summary>
        /// Handles account registration request
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void SignUpMessageHandler(IIncomingMessage message)
        {
            try
            {
                // Get accounts db accessor
                var authDbAccessor = Mst.Server.DbAccessors.GetAccessor<IAccountsDatabaseAccessor>();

                // If guest user cansave its account info
                if (authDbAccessor == null && saveGuestInfo)
                {
                    throw new Exception("Account database accessor is not defined in AuthModule");
                }

                var securityExt = message.Peer.GetExtension<SecurityInfoPeerExtension>();
                var aesKey = securityExt.AesKey;

                if (aesKey == null)
                {
                    // There's no aesKey that client and master agreed upon
                    message.Respond("Insecure request", ResponseStatus.Unauthorized);
                    return;
                }

                var encryptedData = message.AsBytes();
                var decryptedBytesData = Mst.Security.DecryptAES(encryptedData, aesKey);
                var userCredentials = MstProperties.FromBytes(decryptedBytesData);

                if (!userCredentials.Has(MstDictKeys.USER_NAME) || !userCredentials.Has(MstDictKeys.USER_PASSWORD) || !userCredentials.Has(MstDictKeys.USER_EMAIL))
                {
                    message.Respond("Invalid registration request", ResponseStatus.Error);
                    return;
                }

                var userName = userCredentials.AsString(MstDictKeys.USER_NAME);
                var userPassword = userCredentials.AsString(MstDictKeys.USER_PASSWORD);
                var userEmail = userCredentials.AsString(MstDictKeys.USER_EMAIL).ToLower();

                if(userPasswordMinChars > userPassword.Length)
                {
                    message.Respond($"Invalid user password. It must consist at least {userPasswordMinChars} characters", ResponseStatus.Error);
                    return;
                }

                var userExtension = message.Peer.GetExtension<IUserPeerExtension>();

                // If user is already logged in but not as guest
                if (userExtension != null && !userExtension.Account.IsGuest)
                {
                    // Fail, if user is already logged in, and not with a guest account
                    message.Respond("Invalid registration request", ResponseStatus.Error);
                    return;
                }

                // Check if username is valid
                if (!IsUsernameValid(userName))
                {
                    message.Respond("Invalid Username", ResponseStatus.Error);
                    return;
                }

                // Check if there's a forbidden word in username
                if (censorModule != null && censorModule.HasCensoredWord(userName))
                {
                    message.Respond("Forbidden word used in username", ResponseStatus.Error);
                    return;
                }

                // Check if username length is good
                if ((userName.Length < usernameMinChars) || (userName.Length > usernameMaxChars))
                {
                    message.Respond($"Invalid usernanme length. Min length is {usernameMinChars} and max length is {usernameMaxChars}", ResponseStatus.Error);
                    return;
                }

                // Check if email is valid
                if (!IsEmailValid(userEmail))
                {
                    message.Respond("Invalid Email", ResponseStatus.Invalid);
                    return;
                }

                var userAccount = authDbAccessor.CreateAccountInstance();
                userAccount.Username = userName;
                userAccount.Email = userEmail;
                userAccount.IsGuest = false;
                userAccount.Password = Mst.Security.CreateHash(userPassword);

                // Let's set user email as confirmed if confirmation is not required by default
                if (!useEmailConfirmation)
                {
                    userAccount.IsEmailConfirmed = true;
                }

                await authDbAccessor.InsertNewAccountAsync(userAccount);

                OnUserRegisteredEvent?.Invoke(message.Peer, userAccount);

                message.Respond(ResponseStatus.Success);
            }
            catch (Exception e)
            {
                logger.Error(e);
                message.Respond(e.Message, ResponseStatus.Error);
            }
        }

        /// <summary>
        /// Handles a request to log in
        /// </summary>
        /// <param name="message"></param>
        protected virtual async void SignInMessageHandler(IIncomingMessage message)
        {
            logger.Debug($"Signing in user {message.Peer.Id}...");

            var securityExt = message.Peer.GetExtension<SecurityInfoPeerExtension>();
            var aesKey = securityExt.AesKey;

            if (aesKey == null)
            {
                // There's no aesKey that client and master agreed upon
                message.Respond("Insecure request", ResponseStatus.Unauthorized);
                return;
            }

            var encryptedData = message.AsBytes();

            // Decrypt data
            var decryptedBytesData = Mst.Security.DecryptAES(encryptedData, aesKey);

            // Parse user credentials
            var userCredentials = MstProperties.FromBytes(decryptedBytesData);

            // Get auth accessor
            var authDbAccessor = Mst.Server.DbAccessors.GetAccessor<IAccountsDatabaseAccessor>();

            // Trying to get user extension from peer
            var userPeerExtension = message.Peer.GetExtension<IUserPeerExtension>();

            // Initialize account
            IAccountInfoData userAccount = default;

            // Guest Authentication
            if (userCredentials.Has(MstDictKeys.USER_IS_GUEST) && enableGuestLogin)
            {
                try
                {
                    // If user peer has IUserPeerExtension means this user is already logged in
                    if (userPeerExtension != null)
                    {
                        logger.Debug($"User {message.Peer.Id} trying to login, but he is already logged in");
                        message.Respond("You are already logged in", ResponseStatus.Unauthorized);
                        return;
                    }

                    // If guest user can save its account info
                    if (authDbAccessor == null && saveGuestInfo)
                    {
                        throw new Exception("Account database accessor is not defined in AuthModule");
                    }

                    // UserId
                    var userId = userCredentials.AsString(MstDictKeys.USER_ID);

                    if (saveGuestInfo)
                        // Trying to get user account by user id
                        userAccount = await authDbAccessor.GetAccountByIdAsync(userId);

                    // If guest has no account create it
                    if (userAccount == null)
                    {
                        userAccount = authDbAccessor.CreateAccountInstance();
                        userAccount.Username = GenerateGuestUsername();

                        // If guest may save his data
                        if (saveGuestInfo)
                        {
                            // Save account and return its id in DB
                            var accountId = await authDbAccessor.InsertNewAccountAsync(userAccount);

                            // Set account Id if it was not defined earlier
                            userAccount.Id = accountId;
                        }
                    }

                    // Let's save user auth token
                    userAccount.SetToken(tokenExpiresInDays);

                    await authDbAccessor.UpdateAccountAsync(userAccount);
                }
                catch (Exception e)
                {
                    logger.Error(e.Message);
                    message.Respond(e.Message, ResponseStatus.Error);
                    return;
                }
            }
            // Token Authentication
            else if (userCredentials.Has(MstDictKeys.USER_AUTH_TOKEN))
            {
                try
                {
                    // If user peer has IUserPeerExtension means this user is already logged in
                    if (userPeerExtension != null)
                    {
                        logger.Debug($"User {message.Peer.Id} trying to login, but he is already logged in");
                        message.Respond("You are already logged in", ResponseStatus.Unauthorized);
                        return;
                    }

                    // If account accessor is not found
                    if (authDbAccessor == null)
                    {
                        throw new Exception("Account database accessor is not defined in AuthModule");
                    }

                    // Get account by token
                    userAccount = await authDbAccessor.GetAccountByTokenAsync(userCredentials.AsString(MstDictKeys.USER_AUTH_TOKEN));

                    // if no account found
                    if (userAccount == null)
                    {
                        message.Respond("Account does not exist", ResponseStatus.Failed);
                        return;
                    }

                    // If token has expired
                    if (userAccount.IsTokenExpired())
                    {
                        message.Respond("Your session token has expired", ResponseStatus.Unauthorized);
                        return;
                    }

                    // If another session found
                    if (IsUserLoggedInByUsername(userAccount.Username))
                    {
                        // And respond to requester
                        message.Respond("This account is already logged in", ResponseStatus.Unauthorized);
                        return;
                    }

                    // Let's save user auth token
                    userAccount.SetToken(tokenExpiresInDays);

                    await authDbAccessor.UpdateAccountAsync(userAccount);
                }
                catch (Exception e)
                {
                    logger.Error(e.Message);
                    message.Respond(e.Message, ResponseStatus.Error);
                    return;
                }
            }
            // Username / Password authentication
            else if (userCredentials.Has(MstDictKeys.USER_NAME) && userCredentials.Has(MstDictKeys.USER_PASSWORD))
            {
                try
                {
                    // If user peer has IUserPeerExtension means this user is already logged in
                    if (userPeerExtension != null)
                    {
                        logger.Debug($"User { message.Peer.Id} trying to login, but he is already logged in");
                        message.Respond("You are already logged in", ResponseStatus.Unauthorized);
                        return;
                    }

                    // If account accessor is not found
                    if (authDbAccessor == null)
                    {
                        throw new Exception("Account database accessor is not defined in AuthModule");
                    }

                    // Get username
                    var userName = userCredentials.AsString(MstDictKeys.USER_NAME);

                    // Get user password
                    var userPassword = userCredentials.AsString(MstDictKeys.USER_PASSWORD);

                    // If another session found
                    if (IsUserLoggedInByUsername(userName))
                    {
                        // And respond to requester
                        message.Respond("This account is already logged in", ResponseStatus.Unauthorized);
                        return;
                    }

                    // Get account by its username
                    userAccount = await authDbAccessor.GetAccountByUsernameAsync(userName);

                    if (userAccount == null)
                    {
                        // Couldn't find an account with this name
                        message.Respond("Invalid Credentials", ResponseStatus.Unauthorized);
                        return;
                    }

                    if (!Mst.Security.ValidatePassword(userPassword, userAccount.Password))
                    {
                        // Password is not correct
                        message.Respond("Invalid Credentials", ResponseStatus.Unauthorized);
                        return;
                    }

                    // Let's save user auth token
                    userAccount.SetToken(tokenExpiresInDays);

                    await authDbAccessor.UpdateAccountAsync(userAccount);
                }
                catch (Exception e)
                {
                    message.Respond(e.Message, ResponseStatus.Failed);
                    return;
                }
            }
            // Email authentication
            else if (userCredentials.Has(MstDictKeys.USER_EMAIL))
            {
                try
                {
                    // If user peer has IUserPeerExtension means this user is already logged in
                    if (userPeerExtension != null)
                    {
                        logger.Debug($"User { message.Peer.Id} trying to login, but he is already logged in");
                        message.Respond("You are already logged in", ResponseStatus.Unauthorized);
                        return;
                    }

                    // If account accessor is not found
                    if (authDbAccessor == null)
                    {
                        throw new Exception("Account database accessor is not defined in AuthModule");
                    }

                    // Get user email
                    var userEmail = userCredentials.AsString(MstDictKeys.USER_EMAIL);

                    // if email is not in valid format
                    if (!IsEmailValid(userEmail))
                    {
                        throw new Exception("Your email is not valid");
                    }

                    // If another session found
                    if (IsUserLoggedInByUsername(userEmail))
                    {
                        // And respond to requester
                        message.Respond("This account is already logged in", ResponseStatus.Unauthorized);
                        return;
                    }

                    // Get account by its username
                    userAccount = await authDbAccessor.GetAccountByEmailAsync(userEmail);

                    // Create new password
                    string newPassword = Mst.Helper.CreateRandomAlphanumericString(userPasswordMinChars);

                    logger.Debug($"Created new password [{newPassword}] for user [{userEmail}]");

                    // if no account found let's create it
                    if (userAccount == null)
                    {
                        userAccount = authDbAccessor.CreateAccountInstance();
                        userAccount.Username = userEmail;
                        userAccount.Email = userEmail;
                        userAccount.IsGuest = false;
                        userAccount.Password = Mst.Security.CreateHash(newPassword);
                        userAccount.IsEmailConfirmed = true;

                        // Save account and return its id in DB
                        var accountId = await authDbAccessor.InsertNewAccountAsync(userAccount);

                        // Set account Id if it was not defined earlier
                        userAccount.Id = accountId;
                    }
                    // if account found
                    else
                    {
                        userAccount.Password = Mst.Security.CreateHash(newPassword);
                    }

                    // Let's save user auth token
                    userAccount.SetToken(tokenExpiresInDays);

                    await authDbAccessor.UpdateAccountAsync(userAccount);

                    if (mailer == null)
                    {
                        message.Respond("Couldn't send creadentials to your e-mail. Please contact support", ResponseStatus.Error);
                        return;
                    }

                    StringBuilder emailBody = new StringBuilder();
                    emailBody.Append($"<h3>You have requested sign in by email</h3>");
                    emailBody.Append($"<p>Here are your account creadentials</p>");
                    emailBody.Append($"<p><b>Username:</b> {userEmail}</p>");
                    emailBody.Append($"<p><b>Password:</b> {newPassword}</p>");

                    bool sentResult = await mailer.SendMailAsync(userEmail, "Login by Email", emailBody.ToString());

                    if (!sentResult)
                    {
                        message.Respond("Couldn't send creadentials to your e-mail. Please contact support", ResponseStatus.Error);
                        return;
                    }
                }
                catch (Exception e)
                {
                    message.Respond(e.Message, ResponseStatus.Failed);
                    return;
                }
            }

            // if no account found
            if (userAccount == null)
            {
                message.Respond("Invalid request", ResponseStatus.Invalid);
                return;
            }

            // Setup auth extension
            var userExtension = message.Peer.AddExtension(CreateUserPeerExtension(message.Peer));
            userExtension.Account = userAccount;

            // Listen to disconnect event
            userExtension.Peer.OnPeerDisconnectedEvent += OnUserDisconnectedEventListener;

            // Add to lookup of logged in users
            loggedInUsersByUsername.Add(userExtension.Username.ToLower(), userExtension);
            loggedInUsersById.Add(userExtension.UserId, userExtension);

            logger.Debug($"User {message.Peer.Id} signed in as {userAccount.Username}");

            // Send response to logged in user
            message.Respond(userExtension.CreateAccountInfoPacket(), ResponseStatus.Success);

            // Trigger the login event
            OnUserLoggedInEvent?.Invoke(userExtension);
        }

        #endregion
    }
}