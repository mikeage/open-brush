/*
 * Icosa API
 *
 * No description provided (generated by Openapi Generator https://github.com/openapitools/openapi-generator)
 *
 * The version of the OpenAPI document: 0.1.0
 * Generated by: https://github.com/openapitools/openapi-generator.git
 */


using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.IO;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using OpenAPIDateConverter = Org.OpenAPITools.Client.OpenAPIDateConverter;

namespace Org.OpenAPITools.Model
{
    /// <summary>
    /// SubAssetFormat
    /// </summary>
    [DataContract(Name = "SubAssetFormat")]
    public partial class SubAssetFormat : IEquatable<SubAssetFormat>
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SubAssetFormat" /> class.
        /// </summary>
        [JsonConstructorAttribute]
        protected SubAssetFormat() { }
        /// <summary>
        /// Initializes a new instance of the <see cref="SubAssetFormat" /> class.
        /// </summary>
        /// <param name="id">id (required).</param>
        /// <param name="url">url (required).</param>
        /// <param name="format">format (required).</param>
        public SubAssetFormat(string id = default(string), string url = default(string), string format = default(string))
        {
            // to ensure "id" is required (not null)
            if (id == null)
            {
                throw new ArgumentNullException("id is a required property for SubAssetFormat and cannot be null");
            }
            this.Id = id;
            // to ensure "url" is required (not null)
            if (url == null)
            {
                throw new ArgumentNullException("url is a required property for SubAssetFormat and cannot be null");
            }
            this.Url = url;
            // to ensure "format" is required (not null)
            if (format == null)
            {
                throw new ArgumentNullException("format is a required property for SubAssetFormat and cannot be null");
            }
            this.Format = format;
        }

        /// <summary>
        /// Gets or Sets Id
        /// </summary>
        [DataMember(Name = "id", IsRequired = true, EmitDefaultValue = true)]
        public string Id { get; set; }

        /// <summary>
        /// Gets or Sets Url
        /// </summary>
        [DataMember(Name = "url", IsRequired = true, EmitDefaultValue = true)]
        public string Url { get; set; }

        /// <summary>
        /// Gets or Sets Format
        /// </summary>
        [DataMember(Name = "format", IsRequired = true, EmitDefaultValue = true)]
        public string Format { get; set; }

        /// <summary>
        /// Returns the string presentation of the object
        /// </summary>
        /// <returns>String presentation of the object</returns>
        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("class SubAssetFormat {\n");
            sb.Append("  Id: ").Append(Id).Append("\n");
            sb.Append("  Url: ").Append(Url).Append("\n");
            sb.Append("  Format: ").Append(Format).Append("\n");
            sb.Append("}\n");
            return sb.ToString();
        }

        /// <summary>
        /// Returns the JSON string presentation of the object
        /// </summary>
        /// <returns>JSON string presentation of the object</returns>
        public virtual string ToJson()
        {
            return Newtonsoft.Json.JsonConvert.SerializeObject(this, Newtonsoft.Json.Formatting.Indented);
        }

        /// <summary>
        /// Returns true if objects are equal
        /// </summary>
        /// <param name="input">Object to be compared</param>
        /// <returns>Boolean</returns>
        public override bool Equals(object input)
        {
            return this.Equals(input as SubAssetFormat);
        }

        /// <summary>
        /// Returns true if SubAssetFormat instances are equal
        /// </summary>
        /// <param name="input">Instance of SubAssetFormat to be compared</param>
        /// <returns>Boolean</returns>
        public bool Equals(SubAssetFormat input)
        {
            if (input == null)
            {
                return false;
            }
            return 
                (
                    this.Id == input.Id ||
                    (this.Id != null &&
                    this.Id.Equals(input.Id))
                ) && 
                (
                    this.Url == input.Url ||
                    (this.Url != null &&
                    this.Url.Equals(input.Url))
                ) && 
                (
                    this.Format == input.Format ||
                    (this.Format != null &&
                    this.Format.Equals(input.Format))
                );
        }

        /// <summary>
        /// Gets the hash code
        /// </summary>
        /// <returns>Hash code</returns>
        public override int GetHashCode()
        {
            unchecked // Overflow is fine, just wrap
            {
                int hashCode = 41;
                if (this.Id != null)
                {
                    hashCode = (hashCode * 59) + this.Id.GetHashCode();
                }
                if (this.Url != null)
                {
                    hashCode = (hashCode * 59) + this.Url.GetHashCode();
                }
                if (this.Format != null)
                {
                    hashCode = (hashCode * 59) + this.Format.GetHashCode();
                }
                return hashCode;
            }
        }

    }

}
