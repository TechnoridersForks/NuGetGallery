﻿using System;
using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using Crypto = NuGetGallery.CryptographyService;
using NuGetGallery.Configuration;

namespace NuGetGallery
{
    public class UserService : IUserService
    {
        public IAppConfiguration Config { get; protected set; }
        public IEntityRepository<User> UserRepository { get; protected set; }
        public IEntityRepository<Credential> CredentialRepository { get; protected set; }

        protected UserService() { }

        public UserService(
            IAppConfiguration config,
            IEntityRepository<User> userRepository,
            IEntityRepository<Credential> credentialRepository)
            : this()
        {
            Config = config;
            UserRepository = userRepository;
            CredentialRepository = credentialRepository;
        }

        public virtual User Create(
            string username,
            string password,
            string emailAddress)
        {
            // TODO: validate input
            // TODO: consider encrypting email address with a public key, and having the background process that send messages have the private key to decrypt

            var existingUser = FindByUsername(username);
            if (existingUser != null)
            {
                throw new EntityException(Strings.UsernameNotAvailable, username);
            }

            existingUser = FindByEmailAddress(emailAddress);
            if (existingUser != null)
            {
                throw new EntityException(Strings.EmailAddressBeingUsed, emailAddress);
            }

            var hashedPassword = Crypto.GenerateSaltedHash(password, Constants.PBKDF2HashAlgorithmId);

            var newUser = new User(username)
            {
                ApiKey = Guid.NewGuid(),
                EmailAllowed = true,
                UnconfirmedEmailAddress = emailAddress,
                EmailConfirmationToken = Crypto.GenerateToken(),
                HashedPassword = hashedPassword,
                PasswordHashAlgorithm = Constants.PBKDF2HashAlgorithmId,
                CreatedUtc = DateTime.UtcNow
            };

            if (!Config.ConfirmEmailAddresses)
            {
                newUser.ConfirmEmailAddress();
            }

            UserRepository.InsertOnCommit(newUser);
            UserRepository.CommitChanges();

            return newUser;
        }

        public void UpdateProfile(User user, bool emailAllowed)
        {
            if (user == null)
            {
                throw new ArgumentNullException("user");
            }

            user.EmailAllowed = emailAllowed;
            UserRepository.CommitChanges();
        }

        [Obsolete("Use AuthenticateCredential instead")]
        public User FindByApiKey(Guid apiKey)
        {
            return UserRepository.GetAll().SingleOrDefault(u => u.ApiKey == apiKey);
        }

        public virtual User FindByEmailAddress(string emailAddress)
        {
            return UserRepository.GetAll().SingleOrDefault(u => u.EmailAddress == emailAddress);
        }

        public virtual IList<User> FindByUnconfirmedEmailAddress(string unconfirmedEmailAddress, string optionalUsername)
        {
            if (optionalUsername == null)
            {
                return UserRepository.GetAll().Where(u => u.UnconfirmedEmailAddress == unconfirmedEmailAddress).ToList();
            }
            else
            {
                return UserRepository.GetAll().Where(u => u.UnconfirmedEmailAddress == unconfirmedEmailAddress && u.Username == optionalUsername).ToList();
            }
        }

        public virtual User FindByUsername(string username)
        {
            return UserRepository.GetAll()
                .Include(u => u.Roles)
                .SingleOrDefault(u => u.Username == username);
        }

        public virtual User FindByUsernameAndPassword(string username, string password)
        {
            var user = UserRepository.GetAll()
                .Include(u => u.Roles)
                .Include(u => u.Credentials)
                .SingleOrDefault(u => u.Username == username);

            return AuthenticatePassword(password, user);
        }

        public virtual User FindByUsernameOrEmailAddressAndPassword(string usernameOrEmail, string password)
        {
            var user = UserRepository.GetAll()
                .Include(u => u.Roles)
                .Include(u => u.Credentials)
                .SingleOrDefault(u => u.Username == usernameOrEmail || u.EmailAddress == usernameOrEmail);

            return AuthenticatePassword(password, user);
        }

        [Obsolete("Use ReplaceCredential instead")]
        public string GenerateApiKey(string username)
        {
            var user = FindByUsername(username);
            if (user == null)
            {
                return null;
            }

            var newApiKey = Guid.NewGuid();
            user.ApiKey = newApiKey;
            UserRepository.CommitChanges();
            return newApiKey.ToString();
        }

        public void ChangeEmailAddress(User user, string newEmailAddress)
        {
            var existingUser = FindByEmailAddress(newEmailAddress);
            if (existingUser != null && existingUser.Key != user.Key)
            {
                throw new EntityException(Strings.EmailAddressBeingUsed, newEmailAddress);
            }

            user.UpdateEmailAddress(newEmailAddress, Crypto.GenerateToken);
            UserRepository.CommitChanges();
        }

        public bool ChangePassword(string username, string oldPassword, string newPassword)
        {
            // Review: If the old password is hashed using something other than PBKDF2, we end up making an extra db call that changes the old hash password.
            // This operation is rare enough that I'm not inclined to change it.
            var user = FindByUsernameAndPassword(username, oldPassword);
            if (user == null)
            {
                return false;
            }

            ChangePasswordInternal(user, newPassword);
            UserRepository.CommitChanges();
            return true;
        }

        public bool ConfirmEmailAddress(User user, string token)
        {
            if (user == null)
            {
                throw new ArgumentNullException("user");
            }

            if (String.IsNullOrEmpty(token))
            {
                throw new ArgumentNullException("token");
            }

            if (user.EmailConfirmationToken != token)
            {
                return false;
            }

            user.ConfirmEmailAddress();

            UserRepository.CommitChanges();
            return true;
        }

        public virtual User GeneratePasswordResetToken(string usernameOrEmail, int tokenExpirationMinutes)
        {
            if (String.IsNullOrEmpty(usernameOrEmail))
            {
                throw new ArgumentNullException("usernameOrEmail");
            }
            if (tokenExpirationMinutes < 1)
            {
                throw new ArgumentException(
                    "Token expiration should give the user at least a minute to change their password", "tokenExpirationMinutes");
            }

            var user = FindByEmailAddress(usernameOrEmail);
            if (user == null)
            {
                return null;
            }

            if (!user.Confirmed)
            {
                throw new InvalidOperationException(Strings.UserIsNotYetConfirmed);
            }

            if (!String.IsNullOrEmpty(user.PasswordResetToken) && !user.PasswordResetTokenExpirationDate.IsInThePast())
            {
                return user;
            }

            user.PasswordResetToken = Crypto.GenerateToken();
            user.PasswordResetTokenExpirationDate = DateTime.UtcNow.AddMinutes(tokenExpirationMinutes);

            UserRepository.CommitChanges();
            return user;
        }

        public bool ResetPasswordWithToken(string username, string token, string newPassword)
        {
            if (String.IsNullOrEmpty(newPassword))
            {
                throw new ArgumentNullException("newPassword");
            }

            var user = FindByUsername(username);

            if (user != null && user.PasswordResetToken == token && !user.PasswordResetTokenExpirationDate.IsInThePast())
            {
                if (!user.Confirmed)
                {
                    throw new InvalidOperationException(Strings.UserIsNotYetConfirmed);
                }

                ChangePasswordInternal(user, newPassword);
                user.PasswordResetToken = null;
                user.PasswordResetTokenExpirationDate = null;
                UserRepository.CommitChanges();
                return true;
            }

            return false;
        }

        public Credential AuthenticateCredential(string type, string value)
        {
            // Search for the cred
            return CredentialRepository
                .GetAll()
                .Include(c => c.User)
                .SingleOrDefault(c => c.Type == type && c.Value == value);
        }

        public void ReplaceCredential(string userName, Credential credential)
        {
            var user = UserRepository
                .GetAll()
                .Include(u => u.Credentials)
                .SingleOrDefault(u => u.Username == userName);
            if (user == null)
            {
                throw new InvalidOperationException(Strings.UserNotFound);
            }
            ReplaceCredential(user, credential);
        }

        public void ReplaceCredential(User user, Credential credential)
        {
            // Find the credentials we're replacing, if any
            var creds = user.Credentials
                .Where(cred => cred.Type == credential.Type)
                .ToList();
            foreach(var cred in creds)
            {
                user.Credentials.Remove(cred);
            }

            user.Credentials.Add(credential);
            UserRepository.CommitChanges();
        }

        private static User AuthenticatePassword(string password, User user)
        {
            if (user == null)
            {
                return null;
            }

            // Check for a credential
            var cred = user.Credentials
                .FirstOrDefault(c => String.Equals(
                    c.Type,
                    Constants.CredentialTypes.PasswordPbkdf2,
                    StringComparison.OrdinalIgnoreCase));

            bool valid;
            if (cred != null)
            {
                valid = Crypto.ValidateSaltedHash(
                    cred.Value,
                    password,
                    Constants.PBKDF2HashAlgorithmId);
            }
            else
            {
                valid = Crypto.ValidateSaltedHash(
                    user.HashedPassword,
                    password,
                    user.PasswordHashAlgorithm);
            }

            return valid ? user : null;
        }

        private static void ChangePasswordInternal(User user, string newPassword)
        {
            var hashedPassword = Crypto.GenerateSaltedHash(newPassword, Constants.PBKDF2HashAlgorithmId);
            user.PasswordHashAlgorithm = Constants.PBKDF2HashAlgorithmId;
            user.HashedPassword = hashedPassword;
        }
    }
}
